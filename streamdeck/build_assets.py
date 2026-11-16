"""NEBO Stream Deck: the plugin's icons and default key pictures, and the XL profile.

usage: python build_assets.py <plugin folder (...sdPlugin)> <profile out (.streamDeckProfile)>

The keys draw themselves live from the client (Nebo.StreamDeck/KeyArt.cs); these PNGs are what
Stream Deck shows before the plugin runs and in its action list. The profile is the Stream Deck 7
V3 export format (as D:\\Claude\\C130JStreamDeck\\build_profile.py): one page for a Stream Deck XL
(8 x 4, keys "col,row"). Double-click it to import; existing profiles are never touched.
"""
import io
import json
import random
import string
import sys
import uuid
import zipfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

PLUGIN_UUID = "com.kestrelsnest.nebo"
PLUGIN_VERSION = "0.15.0"
DEVICE_MODEL = "20GAT9902"  # Stream Deck XL
APP_VERSION = "7.6.0.23012"

ENG, SRS, MAP, PLAYER = "#2A1414", "#0D1B2E", "#1C2228", "#10305A"


def font(size, bold=True):
    for name in (["segoeuib.ttf", "arialbd.ttf"] if bold else ["segoeui.ttf", "arial.ttf"]):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def key_png(size, back, lines, rounded=True):
    """A key or icon: a filled square with centred lines of (text, scale, colour)."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if rounded:
        d.rounded_rectangle([0, 0, size - 1, size - 1], radius=size // 9, fill=back)
    else:
        d.rectangle([0, 0, size - 1, size - 1], fill=back)
    total = sum(int(size * s) for _, s, _ in lines) + (len(lines) - 1) * size // 20
    y = (size - total) / 2
    for text, scale, colour in lines:
        f = font(max(8, int(size * scale)))
        w = d.textlength(text, font=f)
        d.text(((size - w) / 2, y), text, font=f, fill=colour)
        y += int(size * scale) + size // 20
    buf = io.BytesIO()
    img.save(buf, "PNG")
    return buf.getvalue()


def write_assets(plugin_dir):
    images = Path(plugin_dir) / "images"
    images.mkdir(parents=True, exist_ok=True)
    def save(name, size, back, lines):
        (images / f"{name}.png").write_bytes(key_png(size, back, lines))
        (images / f"{name}@2x.png").write_bytes(key_png(size * 2, back, lines))
    # Plugin and category icons: the NEBO icon, made by branding/make-icons.js into streamdeck/icons.
    icons = Path(__file__).parent / "icons"
    for name in ("plugin", "category"):
        for suffix in ("", "@2x"):
            (images / f"{name}{suffix}.png").write_bytes((icons / f"{name}{suffix}.png").read_bytes())
    save("action_engagement", 20, ENG, [("1", 0.7, "#FFFFFF")])
    save("action_channel", 20, SRS, [("CH", 0.5, "#FFFFFF")])
    save("action_map", 20, MAP, [("M", 0.7, "#FFFFFF")])
    save("action_players", 20, PLAYER, [("P", 0.7, "#FFFFFF")])
    save("key_engagement", 72, ENG, [("ENG", 0.28, "#9A7A7A")])
    save("key_channel", 72, SRS, [("SRS", 0.28, "#9FB4C8")])
    save("key_map", 72, MAP, [("MAP", 0.28, "#9FB4C8")])
    save("key_players", 72, PLAYER, [("PLAYERS", 0.18, "#FFFFFF"), ("ONLY", 0.18, "#FFFFFF")])


# ---- profile -------------------------------------------------------------------------------

def layout():
    """(pos "col,row", action name, settings, back colour, default caption)."""
    keys = []
    for i in range(8):
        keys.append((f"{i},0", "engagement", {"n": i + 1}, ENG, str(i + 1)))
    for i in range(5):
        keys.append((f"{i},1", "channel", {"ch": i + 1}, SRS, f"CH{i + 1}"))
        keys.append((f"{i},2", "channel", {"ch": i + 6}, SRS, f"CH{i + 6}"))
    keys += [("5,1", "labels", {}, MAP, "LABELS"), ("6,1", "labelpos", {}, MAP, "LABEL POS"),
             ("7,1", "workspace", {}, MAP, "GCI/ATC"), ("5,2", "playersonly", {}, PLAYER, "PLAYERS")]
    for i, (name, caption) in enumerate([("ground", "GROUND"), ("landed", "LANDED"), ("rings", "RINGS"), ("marks", "F10 MARKS"),
                                         ("tgp", "TGP"), ("zones", "ZONES"), ("airfields", "AIRFIELDS")]):
        keys.append((f"{i},3", "filter", {"name": name}, MAP, caption))
    return keys


NAMES = {"engagement": "Engagement", "channel": "SRS channel", "labels": "Labels", "labelpos": "Label position",
         "workspace": "GCI / ATC", "filter": "Map filter", "playersonly": "Players only (hold)"}


def image_name(rng):
    return "".join(rng.choice(string.ascii_uppercase + string.digits) for _ in range(26)) + "Z.png"


def write_profile(out, seed=1):
    rng = random.Random(seed)
    profile = str(uuid.UUID(int=rng.getrandbits(128))).upper()
    page = str(uuid.UUID(int=rng.getrandbits(128)))
    blank = str(uuid.UUID(int=rng.getrandbits(128)))
    root = f"Profiles/{profile}.sdProfile"
    # No custom images on the keys: Stream Deck ignores a plugin's setImage on a key that has one,
    # and the plugin draws every key live (the manifest's images show until it runs).
    actions, files = {}, {}
    for pos, kind, settings, back, caption in layout():
        actions[pos] = {
            "ActionID": str(uuid.UUID(int=rng.getrandbits(128))), "LinkedTitle": True, "Name": NAMES[kind],
            "Plugin": {"Name": "NEBO", "UUID": PLUGIN_UUID, "Version": PLUGIN_VERSION}, "Resources": None,
            "Settings": settings, "State": 0,
            "States": [{"FontFamily": "", "FontSize": 12, "FontStyle": "", "FontUnderline": False,
                        "OutlineThickness": 2, "ShowTitle": False, "Title": "", "TitleAlignment": "middle", "TitleColor": "#ffffff"}],
            "UUID": f"{PLUGIN_UUID}.{kind}",
        }
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("package.json", json.dumps({
            "AppVersion": APP_VERSION, "DeviceModel": DEVICE_MODEL, "DeviceSettings": None, "FormatVersion": 1,
            "OSType": "Windows", "OSVersion": "10.0.26300", "RequiredPlugins": [PLUGIN_UUID]}))
        z.writestr(f"{root}/manifest.json", json.dumps({
            "Device": {"Model": DEVICE_MODEL, "UUID": ""}, "Name": "NEBO",
            "Pages": {"Current": page, "Default": blank, "Pages": [page]}, "Version": "3.0"}))
        z.writestr(f"{root}/Profiles/{blank.upper()}/manifest.json",
                   json.dumps({"Controllers": [{"Actions": {}, "Type": "Keypad"}], "Icon": "", "Name": ""}))
        base = f"{root}/Profiles/{page.upper()}"
        z.writestr(f"{base}/manifest.json", json.dumps({"Controllers": [{"Actions": actions, "Type": "Keypad"}], "Icon": "", "Name": ""}))
        for n, png in files.items():
            z.writestr(f"{base}/Images/{n}", png)
    return out


if __name__ == "__main__":
    write_assets(sys.argv[1])
    print(write_profile(sys.argv[2]), "with", len(layout()), "keys")
