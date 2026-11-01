"""Localize an exported Spire Rewind page with the game's own Chinese names.

usage: localize-replay.py IN.html OUT.html [--game-dir DIR] [--simplified]

Reads the official zhs and eng tables straight from the player's own SlayTheSpire2.pck (read-only; nothing from the game
is stored in this repo), maps card/relic/potion/power/monster ids and English names, intents, actions and event option
keys to Chinese, and converts to Traditional Chinese with the Windows LCMapStringEx API unless --simplified is given
(the fixed phrases written here in Traditional are then converted to Simplified the same way).
The page's own interface strings are untouched; only the embedded replay data changes.
"""
import argparse
import ctypes
import json
import os
import re
import struct
import sys

DEFAULT_GAME = "C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2"
TABLES = ("cards", "relics", "potions", "powers", "monsters", "characters", "intents", "encounters",
          "events", "ancients", "enchantments", "afflictions", "card_keywords", "orbs")
ROOMS = {"Monster": "普通戰", "Elite": "精英", "RestSite": "休息處", "Shop": "商店", "Merchant": "商店", "Unknown": "未知",
         "Treasure": "寶箱", "Boss": "Boss", "Event": "事件", "Ancient": "先古"}
PHRASES = {"Confirm selection": "確認選擇", "Proceed": "繼續", "End turn": "結束回合", "Skip card reward": "跳過卡牌獎勵",
           "Add a card to your deck.": "加入一張卡牌", "Open chest": "開寶箱", "Open shop": "進入商店", "Close shop": "離開商店",
           "Rest": "休息", "Smith": "鍛造"}
ACTIONS = {"play_card": "出牌", "end_turn": "結束回合", "use_potion": "使用藥水", "combat_select_card": "選牌",
           "combat_confirm_selection": "確認選擇", "confirm_selection": "確認選擇", "select_card": "選牌",
           "select_card_reward": "選卡牌獎勵", "skip_card_reward": "跳過卡牌獎勵", "claim_reward": "領取獎勵",
           "choose_map_node": "選擇路線", "choose_event_option": "事件選項", "choose_rest_option": "休息處",
           "shop_purchase": "購買", "open_shop": "進入商店", "close_shop": "離開商店", "proceed": "繼續",
           "discard_potion": "丟棄藥水", "open_chest": "開寶箱", "claim_treasure_relic": "拿取遺物",
           "menu_select": "選單"}


def pck_tables(game_dir):
    path = os.path.join(game_dir, "SlayTheSpire2.pck")
    f = open(path, "rb")
    if f.read(4) != b"GDPC":
        raise SystemExit(f"not a Godot pack: {path}")
    fmt = struct.unpack("<4I", f.read(16))[0]
    flags, base = struct.unpack("<IQ", f.read(12))
    if fmt >= 3:
        f.seek(struct.unpack("<Q", f.read(8))[0])
    else:
        f.read(64)
    if flags & 1:
        raise SystemExit("the pack directory is encrypted")
    want = {f"localization/{lang}/{t}.json": (lang, t) for lang in ("zhs", "eng") for t in TABLES}
    found = {}
    entries = []
    for _ in range(struct.unpack("<I", f.read(4))[0]):
        name = f.read(struct.unpack("<I", f.read(4))[0]).rstrip(b"\0").decode("utf-8").replace("res://", "")
        offset, size = struct.unpack("<QQ", f.read(16))
        f.read(16)
        eflags = struct.unpack("<I", f.read(4))[0]
        if name in want and not eflags & 1:
            entries.append((want[name], base + offset, size))
    for (lang, table), offset, size in entries:
        f.seek(offset)
        found.setdefault(lang, {})[table] = json.loads(f.read(size).decode("utf-8"))
    return found


def lcmap(flag):
    """Windows LCMapStringEx Chinese conversion: 0x04000000 to Traditional, 0x02000000 to Simplified."""
    if sys.platform != "win32":
        return lambda s: s
    fn = ctypes.windll.kernel32.LCMapStringEx
    fn.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_wchar_p, ctypes.c_int, ctypes.c_wchar_p, ctypes.c_int,
                   ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]

    def convert(s):
        if not s:
            return s
        buf = ctypes.create_unicode_buffer(len(s) * 2 + 8)
        n = fn("zh-TW" if flag == 0x04000000 else "zh-CN", flag, s, len(s), buf, len(buf), None, None, None)
        return buf.value[:n] if n else s
    return convert


def clean(text):
    return re.sub(r"\[/?[a-z_]+[^\]]*\]", "", text or "").strip()


class Names:
    def __init__(self, tables, conv):
        zh, en = tables["zhs"], tables["eng"]
        self.ids, self.english, self.keys = {}, {}, {}
        for table in TABLES:
            z, e = zh.get(table, {}), en.get(table, {})
            for key, value in z.items():
                if not isinstance(value, str):
                    continue
                self.keys[key] = conv(clean(value))
                if key.endswith(".title") or key.endswith(".name"):
                    model = key.rsplit(".", 1)[0]
                    name = conv(re.sub(r"\s*#?C?\{Count\}", "", clean(value)).strip())
                    if "." not in model and table not in ("intents",):
                        self.ids.setdefault(model, name)
                    eng = re.sub(r"\s*#?C?\{Count\}", "", clean(e.get(key, ""))).strip()
                    if eng and "{" not in eng and table not in ("intents",):
                        self.english.setdefault(eng, name)
        self.intents = {k.split(".")[0]: v for k, v in self.keys.items() if k.endswith(".title") and k.split(".")[0].isupper()
                        and k.count(".") == 1 and k in {f"{x}.title" for x in ("ATTACK", "BUFF", "DEBUFF", "DEFEND", "CARD_DEBUFF")}}
        self.english.setdefault("The Silent", self.ids.get("SILENT", "靜默獵手"))
        self.pattern = re.compile(r"\b(" + "|".join(re.escape(n) for n in sorted(
            (n for n in self.english if len(n) >= 4), key=len, reverse=True)) + r")\b")

    out = staticmethod(lambda s: s)

    def card(self, value):
        plus = value.endswith("+")
        base = value[:-1] if plus else value
        name = self.ids.get(base) or self.english.get(base)
        return (name + ("+" if plus else "")) if name else value

    def label(self, value):
        m = re.match(r"^(.*?)(\s*#\S+)?$", value or "")
        base = m.group(1)
        name = self.english.get(base) or self.ids.get(base)
        return name if name else (self.phrase(value) or self.text(value))

    def phrase(self, value):
        """Fixed UI phrases, map nodes like 'Monster (3,1)' and gold amounts; None when not one of them."""
        if not isinstance(value, str):
            return None
        if value in PHRASES:
            return self.out(PHRASES[value])
        m = re.match(r"^(\w+) \((\d+),(\d+)\)$", value)
        if m and m.group(1) in ROOMS:
            return self.out(ROOMS[m.group(1)]) + f" ({m.group(2)},{m.group(3)})"
        m = re.match(r"^(\d+) Gold$", value)
        if m:
            return self.out(f"{m.group(1)} 金幣")
        m = re.match(r"^card_removal \((\d+) gold\)$", value)
        if m:
            return self.out(f"刪牌（{m.group(1)} 金幣）")
        return None

    def text(self, value):
        if not isinstance(value, str) or not value:
            return value
        return self.pattern.sub(lambda m: self.english[m.group(1)], value)

    def action(self, value):
        if not isinstance(value, str):
            return value
        head, sep, rest = value.partition(" · ")
        verb = self.out(ACTIONS.get(head, head))
        if not sep:
            return verb
        if self.phrase(rest):
            return verb + " · " + self.phrase(rest)
        parts = [self.card(p.strip()) if p.strip() else p for p in re.split(r"\s*(?:→|->)\s*", rest)]
        return verb + " · " + " → ".join(self.label(p) if p == q else p for p, q in zip(parts, re.split(r"\s*(?:→|->)\s*", rest)))

    def intent(self, raw):
        try:
            item = json.loads(raw)
        except (TypeError, ValueError):
            return raw
        kind = item.get("type")
        title = self.intents.get((kind or "").upper()) or self.keys.get(f"{(kind or '').upper()}.title") or item.get("title")
        dmg, rep = item.get("damage"), item.get("repeats")
        desc = f"{title}" + (f" {dmg}" if dmg is not None else "") + (f"×{rep}" if rep and rep > 1 else "")
        item.update(title=title, description=desc, label=desc)
        return json.dumps(item, ensure_ascii=False)


def localize(data, names):
    for replay in data.get("replays") or []:
        for frame in (replay.get("snapshots") or {}).get("frames") or []:
            for who in (frame.get("players") or []) + (frame.get("enemies") or []):
                if isinstance(who.get("label"), str):
                    who["label"] = names.label(who["label"])
                if isinstance(who.get("intent"), list):
                    who["intent"] = [names.intent(x) for x in who["intent"]]
            if isinstance(frame.get("cards"), list):
                frame["cards"] = [names.card(c) if isinstance(c, str) else c for c in frame["cards"]]
            frame["action"] = names.action(frame.get("action"))
            frame["ai_reason"] = names.text(frame.get("ai_reason"))
    for choice in data.get("choices") or []:
        key = choice.get("text_key")
        zh = names.keys.get(f"{key}.title") or names.keys.get(key or "") if key else None
        choice["value"] = zh or (names.card(choice["value"]) if isinstance(choice.get("value"), str) and choice.get("model_id")
                                 else names.label(choice.get("value")) if isinstance(choice.get("value"), str) else choice.get("value"))
        choice["action"] = names.out(ACTIONS[choice["action"]]) if choice.get("action") in ACTIONS else choice.get("action")
        choice["ai_reason"] = names.text(choice.get("ai_reason"))
    ai = data.get("ai_run") or {}
    for k in ("summary",):
        if isinstance(ai.get(k), str):
            ai[k] = names.text(ai[k])
    return data


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("dst")
    ap.add_argument("--game-dir", default=os.environ.get("STS2_GAME_DIR", DEFAULT_GAME))
    ap.add_argument("--simplified", action="store_true")
    a = ap.parse_args()
    html = open(a.src, encoding="utf-8").read()
    m = re.search(r'(<script id="replay-data" type="application/json">)(.*?)(</script>)', html, re.S)
    if not m:
        raise SystemExit("no embedded replay data")
    conv = (lambda s: s) if a.simplified else lcmap(0x04000000)
    names = Names(pck_tables(a.game_dir), conv)
    names.out = lcmap(0x02000000) if a.simplified else (lambda s: s)
    data = localize(json.loads(m.group(2)), names)
    data["language"] = "zhs" if a.simplified else "zht"
    payload = json.dumps(data, ensure_ascii=False).replace("</", "<\\/")
    out = html[:m.start(2)] + payload + html[m.end(2):]
    open(a.dst, "w", encoding="utf-8", newline="").write(out)
    print(json.dumps({"out": a.dst, "ids": len(names.ids), "english": len(names.english)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
