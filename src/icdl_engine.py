"""
iCloud Downloader engine: the download side of the GUI fork of icloudpd.

It signs in (saved session, saved password, or asks the GUI), lists the whole iCloud Photos
library, skips every file already in the destination folder (the same names and folders as
icloudpd 1.33 with --folder-structure {:%Y/%m/%d}, so earlier runs are recognised), and downloads
the rest in parallel. Partial downloads (*.part) are resumed.

It talks to the GUI with one JSON object per line: events on stdout, messages on stdin:
answers ({"password": "...", "remember": true}, {"code": "123456"}, {"cancel": true}) and
commands ({"workers": 8} changes the number of parallel downloads while running).

    python icdl_engine.py --mode download --user name@example.com --dir D:\\Photos --cookies <dir> --workers 6
    python icdl_engine.py --mode login    --user name@example.com --cookies <dir>
    python icdl_engine.py --mode logout   --user name@example.com --cookies <dir>
"""

from __future__ import annotations

import argparse
import errno
import json
import logging
import os
import queue
import re
import shutil
import sys
import threading
import time
import traceback
from concurrent.futures import FIRST_COMPLETED, Future, ThreadPoolExecutor, wait
from functools import partial
from typing import Any, Dict, List, NamedTuple
from urllib.parse import urlencode

from requests.exceptions import RequestException
from tzlocal import get_localzone

from icloudpd import download as icloudpd_download
from icloudpd.base import (
    build_filename_cleaner,
    download_builder,
    lp_filename_concatinator,
)
from icloudpd.counter import Counter
from icloudpd.filename_policies import create_filename_builder
from pyicloud_ipd.base import PyiCloudService
from pyicloud_ipd.exceptions import (
    PyiCloudAPIResponseException,
    PyiCloudConnectionErrorException,
    PyiCloudException,
    PyiCloudFailedLoginException,
    PyiCloudFailedMFAException,
    PyiCloudServiceNotActivatedException,
    PyiCloudServiceUnavailableException,
)
from pyicloud_ipd.file_match import FileMatchPolicy
from pyicloud_ipd.raw_policy import RawTreatmentPolicy
from pyicloud_ipd.utils import (
    delete_password_in_keyring,
    get_password_from_keyring,
    store_password_in_keyring,
)
from pyicloud_ipd.version_size import AssetVersionSize, LivePhotoVersionSize

MAX_WORKERS = 32
LANG = "ru"


def T(ru: str, en: str) -> str:
    """The message in the GUI's language."""
    return en if LANG == "en" else ru


# ---------------------------------------------------------------- talking to the GUI

_OUT_LOCK = threading.Lock()


def emit(event: str, **fields: Any) -> None:
    fields["event"] = event
    line = json.dumps(fields, ensure_ascii=False)
    with _OUT_LOCK:
        sys.stdout.write(line + "\n")
        sys.stdout.flush()


_ANSWERS: "queue.Queue[Dict[str, Any]]" = queue.Queue()


class Gate:
    """How many downloads may run at once; the GUI can change it while running."""

    def __init__(self, limit: int) -> None:
        self.cond = threading.Condition()
        self.limit = limit
        self.active = 0

    def set_limit(self, limit: int) -> None:
        with self.cond:
            self.limit = max(1, min(limit, MAX_WORKERS))
            self.cond.notify_all()

    def __enter__(self) -> "Gate":
        with self.cond:
            while self.active >= self.limit:
                self.cond.wait()
            self.active += 1
        return self

    def __exit__(self, *_: Any) -> None:
        with self.cond:
            self.active -= 1
            self.cond.notify_all()


GATE = Gate(6)


class NameLocks:
    """
    Files with the same name are handled one at a time. Duplicates in a library share a checksum, and icloudpd names
    the partial file after it, so two threads would write one .part file; one at a time, the second one is seen as
    already downloaded (or saved with its size appended, as icloudpd does).
    """

    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.locks: Dict[str, threading.Lock] = {}

    def get(self, name: str) -> threading.Lock:
        with self.lock:
            return self.locks.setdefault(name.lower(), threading.Lock())


NAME_LOCKS = NameLocks()


class Throttle:
    """
    Backs off when iCloud starts refusing: 6 failures within 20 s pause every download for 15 s and take one parallel
    download away (down to 1); again after every such burst, until iCloud stops refusing. After 5 quiet minutes they
    come back one at a time, up to what the user chose.
    """

    BURST, WINDOW, PAUSE, QUIET = 6, 20.0, 15.0, 300.0

    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.failures: List[float] = []
        self.paused_until = 0.0
        self.last_burst = 0.0
        self.last_raise = 0.0
        self.user_limit = 6

    def wait(self) -> None:
        while True:
            left = self.paused_until - time.monotonic()
            if left <= 0:
                return
            time.sleep(min(left, 1.0))

    def failure(self) -> None:
        now = time.monotonic()
        with self.lock:
            self.failures = [t for t in self.failures if now - t < self.WINDOW] + [now]
            if len(self.failures) < self.BURST or now < self.paused_until:
                return
            self.failures.clear()
            self.paused_until = now + self.PAUSE
            self.last_burst = now
            limit = max(1, GATE.limit - 1)
        GATE.set_limit(limit)
        emit("throttled", workers=limit, pause=int(self.PAUSE))
        emit("log", level="warning", message=T(
            f"Apple ограничивает запросы — пауза {int(self.PAUSE)} с и {limit} потока(ов). Скорость вернётся сама.",
            f"Apple is throttling requests — pausing {int(self.PAUSE)} s and using {limit} parallel downloads. Speed comes back by itself."))

    def success(self) -> None:
        now = time.monotonic()
        with self.lock:
            if GATE.limit >= self.user_limit or now - self.last_burst < self.QUIET or now - self.last_raise < 120:
                return
            self.last_raise = now
            limit = GATE.limit + 1
        GATE.set_limit(limit)
        emit("log", level="info", message=T(f"iCloud снова отвечает нормально — потоков: {limit}.", f"iCloud is fine again — parallel downloads: {limit}."))


THROTTLE = Throttle()


class Target(NamedTuple):
    """Where a photo goes: the destination folder, or the hidden photos' folder inside it."""

    root: str
    downloader: Any


def _read_stdin() -> None:
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            message = json.loads(line)
        except ValueError:
            continue
        if "workers" in message:
            THROTTLE.user_limit = int(message["workers"])
            GATE.set_limit(int(message["workers"]))
            emit("workers", workers=GATE.limit)
            emit("log", level="info", message=T(f"Потоков теперь: {GATE.limit}", f"Parallel downloads: {GATE.limit}"))
            continue
        _ANSWERS.put(message)
    # The window is gone (closed or crashed): don't go on downloading unseen. Half-downloaded files stay as .part
    # and are resumed next time.
    _ANSWERS.put({"eof": True})
    time.sleep(0.5)
    os._exit(0)


def ask(event: str, **fields: Any) -> Dict[str, Any]:
    emit(event, **fields)
    answer = _ANSWERS.get()
    if answer.get("eof") or answer.get("cancel"):
        emit("fatal", message=T("Вход отменён.", "Sign-in cancelled."), detail="", retry=False, cancelled=True)
        sys.exit(3)
    return answer


# ---------------------------------------------------------------- readable errors


def explain(error: BaseException) -> str:
    """A plain-language description of what went wrong and what to do about it."""
    text = str(error)
    if isinstance(error, PyiCloudFailedLoginException) or "Invalid username/password" in text:
        return T(
            "Apple не принял Apple ID или пароль. Проверьте их и попробуйте снова.",
            "Apple did not accept the Apple ID or password. Check them and try again.",
        )
    if isinstance(error, PyiCloudFailedMFAException):
        return T(
            "Код подтверждения не подошёл. Запросите новый код и введите его ещё раз.",
            "The verification code was not accepted. Request a new code and enter it again.",
        )
    if isinstance(error, PyiCloudServiceNotActivatedException):
        return T(
            "iCloud не даёт доступ к Фото. Войдите один раз на icloud.com в браузере. Если включена «Расширенная "
            "защита данных», включите на iPhone «Доступ к данным iCloud в веб-версии» (Настройки → Apple ID → iCloud).",
            "iCloud does not give access to Photos. Sign in once at icloud.com in a browser. With Advanced Data "
            "Protection on, enable “Access iCloud Data on the Web” on your iPhone (Settings → Apple ID → iCloud).",
        )
    if isinstance(error, PyiCloudServiceUnavailableException) or "503" in text:
        return T(
            "Сервер iCloud временно недоступен (503). Загрузка продолжится автоматически.",
            "The iCloud server is temporarily unavailable (503). Downloading will resume automatically.",
        )
    if isinstance(error, PyiCloudAPIResponseException):
        if error.code == "ACCESS_DENIED" or "throttle" in text:
            return T(
                "Apple временно ограничил число запросов. Уменьшите число потоков; загрузка продолжится.",
                "Apple is throttling requests. Use fewer parallel downloads; downloading will resume.",
            )
        if error.code in ("421", "450", "500") or "Authentication required" in text:
            return T("Сессия iCloud истекла. Нужно войти заново.", "The iCloud session expired. Please sign in again.")
        return T(f"iCloud вернул ошибку: {text}", f"iCloud returned an error: {text}")
    if isinstance(error, (PyiCloudConnectionErrorException, RequestException, ConnectionError, TimeoutError)):
        return T(
            "Нет связи с iCloud (обрыв или таймаут сети). Загрузка продолжится автоматически.",
            "No connection to iCloud (network drop or timeout). Downloading will resume automatically.",
        )
    if isinstance(error, OSError):
        if error.errno == errno.ENOSPC:
            return T("На диске закончилось место.", "The disk is full.")
        if error.errno in (errno.EACCES, errno.EPERM):
            name = getattr(error, "filename", "") or text
            return T(f"Нет доступа к файлу или папке: {name}", f"No access to the file or folder: {name}")
        return T(f"Ошибка диска: {text}", f"Disk error: {text}")
    return T(f"Непредвиденная ошибка: {type(error).__name__}: {text}", f"Unexpected error: {type(error).__name__}: {text}")


class EventLogHandler(logging.Handler):
    """icloudpd's own log lines: errors and warnings go to the GUI, translated where known."""

    def emit(self, record: logging.LogRecord) -> None:  # noqa: D401
        if record.levelno < logging.WARNING:
            return
        message = record.getMessage()
        friendly = message
        if "IOError while writing file" in message or "run out of disk space" in message:
            friendly = T("Не удалось записать файл (нет места на диске или нет доступа).", "Could not write a file (disk full or no access).")
        elif "Could not connect to iCloud" in message or "Connection lost" in message:
            friendly = T("Обрыв связи при скачивании файла, он будет докачан.", "Connection lost while downloading a file; it will be resumed.")
        elif "retrying after" in message:
            friendly = T("iCloud отказал в скачивании файла, повторяю…", "iCloud refused a download, retrying…")
        elif "Could not download all of" in message:
            friendly = T("Файл скачан не полностью, будет докачан при следующем проходе.", "A file was not downloaded completely; it will be resumed on the next pass.")
        elif "Please try again later" in message:
            reason = re.search(r"\((.*)\)\. Please try again later", message)
            reason_text = reason.group(1) if reason else ""
            if any(word in reason_text for word in ("ACCESS_DENIED", "throttl", "refusing", "503", "429")):
                friendly = T("Apple временно ограничивает запросы — файл будет докачан.", "Apple is throttling requests — the file will be retried.")
            elif reason_text:
                friendly = T(f"iCloud отказал в скачивании ({reason_text}) — файл будет докачан.", f"iCloud refused the download ({reason_text}) — the file will be retried.")
            else:
                friendly = T("Файл не скачался — будет докачан.", "The file did not download — it will be retried.")
        elif "size does not exist for" in message:
            friendly = T("У этого фото нет оригинала в iCloud (пропущено).", "This photo has no original in iCloud (skipped).")
        elif "Could not find URL to download" in message:
            friendly = T("У этого фото нет оригинала в iCloud (пропущено).", "This photo has no original in iCloud (skipped).")
        elif "Session error, re-authenticating" in message:
            friendly = T("Сессия iCloud сбросилась, вхожу заново…", "The iCloud session was reset, signing in again…")
        emit("log", level="error" if record.levelno >= logging.ERROR else "warning", message=friendly, detail=message)


# ---------------------------------------------------------------- progress of files in flight


class Progress(icloudpd_download.ProgressHooks):
    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.bytes = 0
        self.active: Dict[str, List[int]] = {}  # path -> [done, total]

    def start(self, download_path: str, total_size: int, done_size: int) -> None:
        with self.lock:
            self.active[download_path] = [done_size, total_size]

    def chunk(self, download_path: str, size: int) -> None:
        with self.lock:
            self.bytes += size
            entry = self.active.get(download_path)
            if entry:
                entry[0] += size

    def end(self, download_path: str) -> None:
        with self.lock:
            self.active.pop(download_path, None)

    def snapshot(self) -> Dict[str, Any]:
        with self.lock:
            return {
                "bytes": self.bytes,
                "active": [
                    {"path": path, "done": done, "total": total}
                    for path, (done, total) in self.active.items()
                ],
            }


# ---------------------------------------------------------------- listing the library, several pages at once

PREFETCH_PAGES = 12


def list_parallel(album: Any, total: int) -> Any:
    """
    The album's photos in order, like iterating it, but with up to PREFETCH_PAGES page requests in flight.
    Listing is what limits checking an already-downloaded library (one page of 100 takes ~3 s), so this makes
    the check several times faster.

    Pages start at fixed ranks (0, 100, 200…). A page can return fewer assets than asked for, or more (several
    assets sharing one master); a short page's gap is fetched right then, and assets past the next page's start
    are dropped, so every rank is yielded once.
    """
    from pyicloud_ipd.services.photos import PhotoAsset

    page = album.page_size

    def fetch(offset: int) -> tuple[list[Any], Dict[str, Any]]:
        url = f"{album.service_endpoint}/records/query?{urlencode(album.params)}"
        response = album.session.post(
            url,
            data=json.dumps(album._list_query_gen(offset, album.list_type, album.query_filter)),
            headers={"Content-type": "text/plain"},
        ).json()
        assets = [r for r in response["records"] if r["recordType"] == "CPLAsset"]
        masters = {r["recordName"]: r for r in response["records"] if r["recordType"] == "CPLMaster"}
        return assets, masters

    def assets_of(records: list[Any], masters: Dict[str, Any], previous: Dict[str, Any]) -> Any:
        for record in records:
            master_id = record["fields"]["masterRef"]["value"]["recordName"]
            master = masters.get(master_id) or previous.get(master_id)
            if master is not None:
                yield PhotoAsset(master, record)

    offsets = list(range(0, max(total, 1), page))
    previous: Dict[str, Any] = {}
    with ThreadPoolExecutor(max_workers=PREFETCH_PAGES, thread_name_prefix="list") as pool:
        futures: Dict[int, Future[Any]] = {}
        for index, offset in enumerate(offsets):
            for ahead in offsets[index : index + PREFETCH_PAGES]:
                if ahead not in futures:
                    futures[ahead] = pool.submit(fetch, ahead)
            records, masters = futures.pop(offset).result()
            end = offset + page if index + 1 < len(offsets) else None
            records = records[: page] if end is not None else records
            yield from assets_of(records, masters, previous)
            previous = masters
            # A short page: fill the ranks up to the next page right away, one request at a time.
            position = offset + len(records)
            while end is not None and position < end and records:
                records, masters = fetch(position)
                records = records[: end - position]
                yield from assets_of(records, masters, previous)
                previous = {**previous, **masters}
                position += len(records)
            if end is None and len(records) == page:
                # The library grew since it was counted: carry on page by page.
                position = offset + len(records)
                while True:
                    records, masters = fetch(position)
                    if not records:
                        break
                    yield from assets_of(records, masters, previous)
                    previous = masters
                    position += len(records)


# ---------------------------------------------------------------- sign in


def sign_in(user: str, cookies: str) -> PyiCloudService:
    entered: Dict[str, Any] = {}

    def password_provider() -> str | None:
        try:
            saved = get_password_from_keyring(user)
        except Exception:
            saved = None
        if saved and not entered.get("saved_failed"):
            entered["from_keyring"] = True
            return saved
        answer = ask("need_password", user=user, error=entered.get("error", ""))
        entered["password"] = answer.get("password", "")
        entered["remember"] = bool(answer.get("remember"))
        return entered["password"]

    emit("status", stage="login", message=T("Вход в iCloud…", "Signing in to iCloud…"))
    icloud = None
    for _attempt in range(6):
        try:
            icloud = PyiCloudService("com", user, password_provider, cookie_directory=cookies)
            break
        except (PyiCloudFailedLoginException, PyiCloudAPIResponseException) as error:
            bad_password = isinstance(error, PyiCloudFailedLoginException) or (
                "Invalid username/password" in str(error) or getattr(error, "code", None) == "401"
            )
            if not bad_password:
                raise
            if entered.pop("from_keyring", False):
                entered["saved_failed"] = True
                entered["error"] = T("Сохранённый пароль больше не подходит. Введите пароль Apple ID.", "The saved password no longer works. Enter your Apple ID password.")
            else:
                entered["error"] = explain(PyiCloudFailedLoginException(str(error)))
    if icloud is None:
        raise PyiCloudFailedLoginException(T("Слишком много неудачных попыток входа", "Too many failed sign-in attempts"))

    if entered.get("remember") and entered.get("password"):
        try:
            store_password_in_keyring(user, entered["password"])
        except Exception as error:
            emit("log", level="warning", message=T("Не удалось сохранить пароль.", "Could not save the password."), detail=str(error))

    if icloud.requires_2fa:
        if not icloud.trigger_push_notification():
            emit("log", level="info", message=T(
                "Push с кодом не отправился. Код можно получить на iPhone: Настройки → Apple ID → Вход и безопасность → Получить код.",
                "The code push was not sent. Get a code on your iPhone: Settings → Apple ID → Sign-In & Security → Get Verification Code.",
            ))
        error = ""
        for _attempt in range(6):
            answer = ask("need_code", error=error)
            code = re.sub(r"\D", "", str(answer.get("code", "")))
            if len(code) != 6:
                error = T("Код должен состоять из 6 цифр.", "The code has 6 digits.")
                continue
            if icloud.validate_2fa_code(code):
                break
            error = T("Код не подошёл. Введите новый код с устройства Apple.", "Wrong code. Enter a new code from your Apple device.")
        else:
            raise PyiCloudFailedMFAException(T("Код подтверждения не принят", "The verification code was not accepted"))
    elif icloud.requires_2sa:
        raise PyiCloudFailedMFAException(T(
            "Аккаунт использует старую двухэтапную проверку (2SA). Включите двухфакторную аутентификацию в настройках Apple ID.",
            "This account uses the old two-step verification (2SA). Turn on two-factor authentication for the Apple ID.",
        ))

    name = ""
    try:
        info = icloud.data.get("dsInfo", {})
        name = " ".join(p for p in (info.get("firstName"), info.get("lastName")) if p)
    except Exception:
        pass
    emit("login_ok", user=user, name=name)
    return icloud


def sign_out(user: str, cookies: str) -> int:
    try:
        delete_password_in_keyring(user)
    except Exception:
        pass
    base = "".join(c for c in user if re.match(r"\w", c))
    for name in (base, base + ".session"):
        try:
            os.remove(os.path.join(cookies, name))
        except FileNotFoundError:
            pass
    emit("logged_out", user=user)
    return 0


# ---------------------------------------------------------------- download


def run(args: argparse.Namespace) -> int:
    progress = Progress()
    icloudpd_download.HOOKS = progress

    logger = logging.getLogger("icdl")
    logger.setLevel(logging.INFO)
    logger.addHandler(EventLogHandler())
    logger.propagate = False

    icloud = sign_in(args.user, args.cookies)

    filename_builder = create_filename_builder(
        FileMatchPolicy.NAME_SIZE_DEDUP_WITH_SUFFIX, build_filename_cleaner(False)
    )
    def make_target(directory: str) -> Target:
        # The settings of icloudpd's defaults with --folder-structure {:%Y/%m/%d}: original sizes, live photo videos.
        directory = os.path.normpath(directory)
        return Target(directory, partial(
            download_builder,
            logger,
            "{:%Y/%m/%d}",
            directory,
            [AssetVersionSize.ORIGINAL],
            False,  # force_size
            False,  # only_print_filenames
            False,  # set_exif_datetime
            False,  # skip_live_photos
            LivePhotoVersionSize.ORIGINAL,
            False,  # dry_run
            FileMatchPolicy.NAME_SIZE_DEDUP_WITH_SUFFIX,
            False,  # xmp_sidecar
            lp_filename_concatinator,
            filename_builder,
            RawTreatmentPolicy.AS_IS,
            icloud,
            Counter(0),
        ))

    main_target = make_target(args.dir)
    emit("status", stage="counting", message=T("Считаю фото в iCloud…", "Counting photos in iCloud…"))
    album = icloud.photos.all
    main_total = len(album)
    # Hidden photos are not in "All Photos": with --hidden-dir they go to their own folder inside the destination.
    hidden_album, hidden_total, hidden_target = None, 0, None
    if args.hidden_dir:
        try:
            hidden_album = icloud.photos.albums["Hidden"]
            hidden_total = len(hidden_album)
            hidden_target = make_target(os.path.join(args.dir, args.hidden_dir))
        except Exception as error:
            emit("log", level="warning", message=T("Не удалось открыть скрытые фото.", "Could not open the hidden photos."), detail=repr(error))
            hidden_album = None
    total = main_total + hidden_total
    emit("total", total=total)
    if hidden_album is not None:
        emit("log", level="info", message=T(
            f"В iCloud: {main_total} фото и видео и {hidden_total} скрытых (они пойдут в папку {args.hidden_dir}).",
            f"In iCloud: {main_total} photos and videos and {hidden_total} hidden ones (they go to the {args.hidden_dir} folder)."))
    emit("status", stage="running", message=T("Сверяю с уже скачанным и докачиваю остальное…", "Comparing with what is downloaded and fetching the rest…"))

    stats = {"done": 0, "existed": 0, "downloaded": 0, "failed": 0, "missing": 0}
    stats_lock = threading.Lock()
    failed: List[tuple[Any, Target]] = []
    stopping = threading.Event()

    def relocate(photo: Any, root: str) -> None:
        """
        A photo with a repaired date that is already in the folder under its broken date (downloaded by icloudpd or
        an earlier version, e.g. into 1980/01/01): its files move to the right day instead of being downloaded again,
        and the emptied folders go.
        """
        try:
            wrong = photo.asset_date.astimezone(get_localzone())
        except (ValueError, OSError):
            wrong = photo.asset_date
        old_dir = os.path.normpath(os.path.join(root, "{:%Y/%m/%d}".format(wrong)))
        new_dir = os.path.normpath(os.path.join(root, "{:%Y/%m/%d}".format(photo.created)))
        if old_dir == new_dir or not os.path.isdir(old_dir):
            return
        stem = os.path.splitext(filename_builder(photo))[0]
        moved = []
        for name in os.listdir(old_dir):
            # IMG_1.JPG, IMG_1_HEVC.MOV (its live photo video), IMG_1-12345.JPG (a same-named duplicate)
            if not name.lower().startswith(stem.lower()) or name[len(stem):len(stem) + 1] not in (".", "-", "_"):
                continue
            target = os.path.join(new_dir, name)
            if os.path.exists(target):
                continue
            try:
                os.makedirs(new_dir, exist_ok=True)
                shutil.move(os.path.join(old_dir, name), target)
                moved.append(name)
            except OSError as error:
                emit("log", level="warning", message=T(f"Не удалось перенести {name}: {error}", f"Could not move {name}: {error}"))
        if moved:
            prune_empty_dates(root)
            emit("log", level="ok", message=T(
                f"Перенесено из {os.path.relpath(old_dir, root)} в {os.path.relpath(new_dir, root)}: {', '.join(moved)}",
                f"Moved from {os.path.relpath(old_dir, root)} to {os.path.relpath(new_dir, root)}: {', '.join(moved)}"))

    def attempt(photo: Any, target: Target) -> tuple[str, Any]:
        """One try: ("downloaded", paths), ("existed", None), ("failed", reason) or ("missing", None)."""
        try:
            versions = photo.versions
        except Exception as error:
            return "failed", error
        if getattr(photo, "has_bogus_asset_date", False):
            emit("log", level="info", message=T(
                f"{photo.filename}: в файле испорченная дата съёмки ({photo.asset_date:%Y}) — исправлено на {photo.created:%d.%m.%Y %H:%M}.",
                f"{photo.filename}: the file has a broken creation date ({photo.asset_date:%Y}) — corrected to {photo.created:%d.%m.%Y %H:%M}."))
            relocate(photo, target.root)
        if AssetVersionSize.ORIGINAL not in versions:
            # iCloud keeps no original of this photo (damaged or never fully uploaded): nothing to retry.
            return "missing", None
        try:
            name = filename_builder(photo)
        except Exception:
            name = str(getattr(photo, "id", ""))
        try:
            with NAME_LOCKS.get(name):
                THROTTLE.wait()
                with GATE:
                    result = target.downloader(photo)
        except Exception as error:
            THROTTLE.failure()
            return "failed", error
        if result.created_paths:
            THROTTLE.success()
            return "downloaded", result.created_paths
        if result.present:
            return "existed", None
        THROTTLE.failure()
        return "failed", None

    def handle(photo: Any, target: Target, tries: int = 4) -> None:
        """A photo, tried again right away (5 s, 15 s, 30 s) before it is put aside for the next round."""
        outcome, detail = "failed", None
        for index, pause in enumerate([0, 5, 15, 30][:tries]):
            if stopping.wait(pause):
                return
            outcome, detail = attempt(photo, target)
            if outcome != "failed":
                break
            if index + 1 < tries:
                reason = explain(detail) if isinstance(detail, BaseException) else T("файл не скачался", "the file did not download")
                emit("log", level="warning", message=T(f"{_name(photo)}: {reason} Повтор через {[5, 15, 30][index]} с.",
                                                       f"{_name(photo)}: {reason} Retrying in {[5, 15, 30][index]} s."),
                     detail=repr(detail) if detail else None)
        with stats_lock:
            stats["done"] += 1
            if outcome == "downloaded":
                stats["downloaded"] += 1
            elif outcome == "existed":
                stats["existed"] += 1
            elif outcome == "missing":
                stats["missing"] += 1
            else:
                stats["failed"] += 1
                failed.append((photo, target))
        if outcome == "missing":
            emit("log", level="warning", message=T(f"{_name(photo)}: в iCloud нет оригинала этого фото — скачать нечего.",
                                                   f"{_name(photo)}: iCloud has no original of this photo — nothing to download."))
        elif outcome == "failed":
            reason = explain(detail) if isinstance(detail, BaseException) else T("файл не скачался", "the file did not download")
            emit("log", level="error", message=T(f"{_name(photo)}: {reason} Докачаю в следующем круге.",
                                                 f"{_name(photo)}: {reason} Will retry in the next round."),
                 detail=repr(detail) if detail else None)
        elif outcome == "downloaded":
            size = 0
            for path in detail:
                try:
                    size += os.path.getsize(path)
                except OSError:
                    pass
            emit("file", path=detail[0], count=len(detail), size=size)

    stop_ticker = threading.Event()

    def ticker() -> None:
        while not stop_ticker.wait(0.5):
            with stats_lock:
                current = dict(stats)
            emit("progress", **current, **progress.snapshot(), workers=GATE.limit)

    tick = threading.Thread(target=ticker, daemon=True)
    tick.start()

    pending: set[Future[None]] = set()
    try:
        with ThreadPoolExecutor(max_workers=MAX_WORKERS, thread_name_prefix="dl") as pool:
            passes = [(album, main_total, main_target)]
            if hidden_album is not None and hidden_target is not None:
                passes.append((hidden_album, hidden_total, hidden_target))
            for source, count, target in passes:
                if target is hidden_target:
                    emit("status", stage="running", message=T(
                        f"Скрытые фото → папка {args.hidden_dir}…", f"Hidden photos → the {args.hidden_dir} folder…"))
                for photo in list_parallel(source, count):
                    pending.add(pool.submit(handle, photo, target))
                    # Keep the queue short, so listing the library never runs far ahead of the downloads.
                    while len(pending) >= GATE.limit * 4:
                        _done, pending = wait(pending, return_when=FIRST_COMPLETED)
            wait(pending)

            # Rounds over the files that failed, until every one is downloaded. Gives up only after five rounds
            # in a row in which not a single file succeeded (then the window restarts the whole run later).
            round_no, fruitless = 0, 0
            while failed and fruitless < 5:
                round_no += 1
                retry = list(failed)
                failed.clear()
                with stats_lock:
                    stats["failed"] -= len(retry)
                    stats["done"] -= len(retry)
                pause = min(300, 30 * 2 ** min(round_no - 1, 4))
                emit("status", stage="retry", message=T(
                    f"Докачиваю файлы с ошибками: {len(retry)} (круг {round_no}, через {pause} с)",
                    f"Retrying failed files: {len(retry)} (round {round_no}, in {pause} s)"))
                if stopping.wait(pause):
                    break
                list(pool.map(lambda item: handle(item[0], item[1], tries=2), retry))
                fruitless = fruitless + 1 if len(failed) >= len(retry) else 0
    finally:
        stop_ticker.set()
        tick.join(1)
        with stats_lock:
            current = dict(stats)
        emit("progress", **current, **progress.snapshot(), workers=GATE.limit)
    for target in (main_target, hidden_target):
        if target is not None:
            try:
                prune_empty_dates(target.root)
            except OSError:
                pass
    emit("done", total=total, **current, failed_names=[_name(p) for p, _target in failed[:200]])
    return 0 if not failed else 4


def prune_empty_dates(root: str) -> None:
    """
    Removes empty YYYY/MM/DD folders (left by files moved to their right date). Only all-digit folder names are
    touched, and only when empty; Windows may hold a folder for a moment (Explorer, antivirus), so each is tried a
    few times.
    """
    for year in os.listdir(root) if os.path.isdir(root) else []:
        year_dir = os.path.join(root, year)
        if not (year.isdigit() and len(year) == 4 and os.path.isdir(year_dir)):
            continue
        for current, dirs, files in os.walk(year_dir, topdown=False):
            if files or any(os.path.isdir(os.path.join(current, d)) for d in dirs):
                continue
            if not os.path.basename(current).isdigit():
                continue
            for _try in range(3):
                try:
                    os.rmdir(current)
                    break
                except OSError:
                    time.sleep(0.3)


def _name(photo: Any) -> str:
    try:
        return f"{photo.filename} ({photo.created:%d.%m.%Y})"
    except Exception:
        return getattr(photo, "id", "photo")


def main() -> int:
    global LANG
    sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)  # type: ignore[attr-defined]
    sys.stdin.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["download", "login", "logout"], default="download")
    parser.add_argument("--user", required=True)
    parser.add_argument("--dir", default="")
    parser.add_argument("--cookies", required=True)
    parser.add_argument("--workers", type=int, default=6)
    parser.add_argument("--lang", choices=["ru", "en"], default="ru")
    parser.add_argument("--hidden-dir", default="",
                        help="also download hidden photos, into this folder inside --dir (e.g. Hidden)")
    parser.add_argument("--videos-by-name", action="store_true",
                        help="an existing video counts as downloaded by its name alone (it may have been compressed)")
    args = parser.parse_args()
    if args.videos_by_name:
        from icloudpd import base as icloudpd_base

        icloudpd_base.SIZE_IGNORED_EXTENSIONS.update({".mov", ".mp4", ".m4v", ".avi", ".3gp", ".mkv", ".webm"})
    LANG = args.lang
    GATE.set_limit(args.workers)
    THROTTLE.user_limit = args.workers

    threading.Thread(target=_read_stdin, daemon=True).start()

    # Quiet the library's own console logging; everything the user needs arrives as events.
    logging.basicConfig(level=logging.CRITICAL)

    if args.mode == "logout":
        return sign_out(args.user, args.cookies)

    try:
        if args.mode == "login":
            sign_in(args.user, args.cookies)
            return 0
        try:
            os.makedirs(args.dir, exist_ok=True)
        except OSError as error:
            emit("fatal", message=explain(error), detail=repr(error), retry=False)
            return 2
        return run(args)
    except (PyiCloudFailedLoginException, PyiCloudFailedMFAException, PyiCloudServiceNotActivatedException) as error:
        emit("fatal", message=explain(error), detail=repr(error), retry=False, auth=True)
        return 3
    except (PyiCloudException, RequestException, OSError) as error:
        emit("fatal", message=explain(error), detail=traceback.format_exc(), retry=True)
        return 5
    except Exception as error:
        emit("fatal", message=explain(error), detail=traceback.format_exc(), retry=True)
        return 6


if __name__ == "__main__":
    sys.exit(main())
