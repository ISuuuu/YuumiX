"""从原项目真实配置生成脱敏测试夹具。

只替换可识别的个人标识（安装路径、SignalR 用户 ID），保留全部字段与值，
因为被测对象是「字段是否无损」，而不是具体路径内容。

用法：python gen_fixture.py
"""
import json
import os
import pathlib

BS = chr(92)  # 反斜杠，避免命令行转义把路径吃掉

src = pathlib.Path(os.environ["APPDATA"]) / "Yuumi" / "config.json"
data = json.loads(src.read_text(encoding="utf-8-sig"))

data["General"]["LolPath"] = [
    "WeGame",
    f"C:{BS}Riot Games{BS}LeagueClient",
]
data["General"]["WegamePath"] = f"C:{BS}Program Files (x86){BS}WeGame"
data["General"]["SignalrUserId"] = "fixture-user"

dst = pathlib.Path(__file__).parent / "config.v3.json"
dst.parent.mkdir(parents=True, exist_ok=True)

# 与原 Rust 的 to_string_pretty 一致：LF 换行、两空格缩进、UTF-8 无 BOM
text = json.dumps(data, indent=2, ensure_ascii=False) + "\n"
dst.write_bytes(text.encode("utf-8"))

print(f"wrote {dst} ({dst.stat().st_size} bytes)")
print("LolPath =", data["General"]["LolPath"])
