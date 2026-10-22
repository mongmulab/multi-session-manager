"""시험용 Claude 설정 폴더를 만든다(CLAUDE_CONFIG_DIR 로 쓴다).

사용자 설정(~/.claude)의 플러그인·스킬·명령·상태줄·훅 구성을 그대로 옮기되,
훅과 상태줄이 실제로 부르는 프로그램은 기록기로 바꾼다. 진짜 훅은 Hub·Orca·색인기로
세션을 올리므로, 시험 세션이 거기 섞이면 안 된다.

자격 증명은 옮기지 않는다(.credentials.json 의 갱신 토큰을 같이 쓰면 원래 세션이 로그아웃될 수 있다).
시험 때 CLAUDE_CODE_OAUTH_TOKEN 으로 따로 넣는다.

    python clone_config.py <대상 폴더> <신뢰할 폴더>...
"""
import json
import os
import shutil
import stat
import sys
from pathlib import Path

SRC = Path.home() / ".claude"
RECORDER = r'''import json, os, sys, time
here = os.path.dirname(os.path.abspath(__file__))
kind = sys.argv[1]
raw = sys.stdin.read()
try:
    data = json.loads(raw)
except ValueError:
    data = {}
row = {"t": time.strftime("%H:%M:%S"), "kind": kind, "event": data.get("hook_event_name"),
       "session": data.get("session_id"), "cwd": data.get("cwd") or (data.get("workspace") or {}).get("current_dir"),
       "tool": data.get("tool_name"), "reason": data.get("reason")}
# 한 건에 파일 하나. 네 칸이 한꺼번에 끝나면 훅도 동시에 돌고, Windows 의 덧붙여 쓰기는 서로 덮어쓴다.
out = os.path.join(here, "hook-records")
os.makedirs(out, exist_ok=True)
with open(os.path.join(out, f"{time.time_ns()}-{os.getpid()}.json"), "w", encoding="utf-8") as f:
    json.dump(row, f, ensure_ascii=False)
if kind == "statusline":
    print("MSM-STATUSLINE " + os.path.basename((row["cwd"] or "").rstrip("\\/")))
'''


def main(dest: Path, trusted: list[str]) -> None:
    if dest.exists():
        # 마켓플레이스의 .git 팩 파일은 읽기 전용이라 그냥은 안 지워진다.
        shutil.rmtree(dest, onexc=lambda f, p, _: (os.chmod(p, stat.S_IWRITE), f(p)))
    dest.mkdir(parents=True)
    (dest / "record.py").write_text(RECORDER, encoding="utf-8")
    rec = (dest / "record.py").as_posix()

    settings = json.loads((SRC / "settings.json").read_text(encoding="utf-8"))
    # 이벤트·매처 구성은 그대로 두고, 각 이벤트에서 부르는 것만 기록기 하나로 바꾼다.
    settings["hooks"] = {
        event: [{**({"matcher": g["matcher"]} if "matcher" in g else {}),
                 "hooks": [{"type": "command", "command": f'python "{rec}" hook', "timeout": 10}]}
                for g in groups]
        for event, groups in settings.get("hooks", {}).items()
    }
    if "statusLine" in settings:
        settings["statusLine"] = {"type": "command", "command": f'python "{rec}" statusline'}
    settings["model"] = "haiku"
    settings["autoUpdatesChannel"] = "stable"
    settings.pop("autoMode", None)
    (dest / "settings.json").write_text(json.dumps(settings, ensure_ascii=False, indent=2), encoding="utf-8")

    for name in ("skills", "commands", "agents"):
        if (SRC / name).is_dir():
            shutil.copytree(SRC / name, dest / name)
    if (SRC / "CLAUDE.md").is_file():
        shutil.copy2(SRC / "CLAUDE.md", dest / "CLAUDE.md")

    # 플러그인: 설치본과 마켓플레이스를 옮기고, 안에 적힌 절대 경로를 새 폴더로 바꾼다.
    plugins = dest / "plugins"
    plugins.mkdir()
    for name in ("cache", "marketplaces"):
        if (SRC / "plugins" / name).is_dir():
            shutil.copytree(SRC / "plugins" / name, plugins / name)
    old, new = str(SRC), str(dest)
    for name in ("installed_plugins.json", "known_marketplaces.json"):
        path = SRC / "plugins" / name
        if path.is_file():
            text = path.read_text(encoding="utf-8")
            for o, n in ((old, new), (old.replace("\\", "\\\\"), new.replace("\\", "\\\\")),
                         (Path(old).as_posix(), Path(new).as_posix())):
                text = text.replace(o, n)
            (plugins / name).write_text(text, encoding="utf-8")

    # 첫 실행 안내(테마 고르기)와 폴더 신뢰 묻기를 건너뛴다. MCP 서버는 옮기지 않는다.
    real = json.loads((Path.home() / ".claude.json").read_text(encoding="utf-8"))
    state = {
        "hasCompletedOnboarding": True,
        "lastOnboardingVersion": real.get("lastOnboardingVersion"),
        "lastReleaseNotesSeen": real.get("lastReleaseNotesSeen"),
        "numStartups": 1,
        "projects": {Path(p).as_posix(): {"hasTrustDialogAccepted": True, "allowedTools": []} for p in trusted},
    }
    (dest / ".claude.json").write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"시험 설정 → {dest} · 훅 {len(settings['hooks'])}종 · 신뢰 폴더 {len(trusted)}개")


if __name__ == "__main__":
    main(Path(sys.argv[1]), sys.argv[2:])
