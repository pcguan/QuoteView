"""HTTP with an explicit proxy, because the environment only half-sets one.

`HTTP_PROXY` is set here but `HTTPS_PROXY` is not, and every source we need is
https. urllib would therefore go direct and hang — the same trap that made `git
push` hang until the proxy was configured per-repository. So the proxy is read
from either variable and installed explicitly rather than left to autodetection.

Standard library only, deliberately: this runs from cron with no venv guarantees,
and one less dependency is one less thing that breaks unattended.
"""

from __future__ import annotations

import gzip
import os
import time
import urllib.error
import urllib.request

# A browser UA. Several sources (财联社 answers 418, Yahoo 429) reject the
# default python-urllib agent outright.
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36")


def _proxy() -> str | None:
    for name in ("HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy"):
        value = os.environ.get(name)
        if value:
            return value
    return None


def _opener() -> urllib.request.OpenerDirector:
    proxy = _proxy()
    handlers = []
    if proxy:
        handlers.append(urllib.request.ProxyHandler({"http": proxy, "https": proxy}))
    return urllib.request.build_opener(*handlers)


OPENER = _opener()


class FetchError(Exception):
    """Any failure to obtain a body. Callers record N/A rather than guessing."""


def get(url: str, *, referer: str | None = None, timeout: int = 20,
        attempts: int = 3, encoding: str = "utf-8") -> str:
    """Fetch a URL as text, retrying transient failures.

    Raises FetchError after the last attempt. Nothing here ever returns a
    partial or substituted body — a caller that gets an exception must write
    N/A, never a guess.
    """
    headers = {"User-Agent": UA, "Accept-Encoding": "gzip"}
    if referer:
        headers["Referer"] = referer

    last: Exception | None = None

    for attempt in range(1, attempts + 1):
        try:
            request = urllib.request.Request(url, headers=headers)
            with OPENER.open(request, timeout=timeout) as response:
                raw = response.read()
                if response.headers.get("Content-Encoding") == "gzip":
                    raw = gzip.decompress(raw)
                return raw.decode(encoding, errors="replace")
        except Exception as exc:  # noqa: BLE001 - all failures are equivalent here
            last = exc
            if attempt < attempts:
                time.sleep(0.6 * attempt)

    raise FetchError(f"{url} -> {type(last).__name__}: {last}") from last


def post(url: str, data: dict, *, referer: str | None = None, timeout: int = 20,
         attempts: int = 2, encoding: str = "utf-8") -> str:
    """Form POST. cninfo's announcement query only answers to POST."""
    import urllib.parse

    body = urllib.parse.urlencode(data).encode()
    headers = {
        "User-Agent": UA,
        "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8",
        "X-Requested-With": "XMLHttpRequest",
    }
    if referer:
        headers["Referer"] = referer

    last: Exception | None = None
    for attempt in range(1, attempts + 1):
        try:
            request = urllib.request.Request(url, data=body, headers=headers, method="POST")
            with OPENER.open(request, timeout=timeout) as response:
                return response.read().decode(encoding, errors="replace")
        except Exception as exc:  # noqa: BLE001
            last = exc
            if attempt < attempts:
                time.sleep(0.6 * attempt)

    raise FetchError(f"POST {url} -> {type(last).__name__}: {last}") from last
