#!/usr/bin/env python3
"""B21 item 20: the client NPC option-text reader (build_npc_menu_text.py), the page join (modules/_npc_menu.py) and
the client patch tool (tools/apply_npc_menu_text.py).  python3 -m pytest -q tests/test_npc_menu_text_unit.py"""
import json
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ED = os.path.dirname(HERE)
sys.path.insert(0, ED)
sys.path.insert(0, os.path.join(ED, "tools"))
import build_npc_menu_text as B  # noqa: E402
from modules import _npc_menu as MENU  # noqa: E402
from modules.common import Bad  # noqa: E402
import apply_npc_menu_text as APPLY  # noqa: E402

LUATEXT = '''LuaText = {}
NF_L0_1 = "|cffFFFF00*Go to A|cFFFFFFFF"
NF_L0_2 = "|cffFFFF00*Go to B|cFFFFFFFF"
NF_L0_001 = "Where do you want to go?"
--~ NF_L0_2 = "commented out"
LuaText.NF_EM_B15 = "|cffFFFF00*Wonderland|cFFFFFFFF"
Msg = { [1] = "not a key" }
'''
DISPATCH = '''NPC_FLAG_SYS_TRANMIT = 1  --x
NPC_FLAG_SYS_REPETITION = 9
function Set_NpcFun_Text(Type,Index,BtnID,SubID)
	if Type == NPC_FLAG_SYS_TRANMIT then
		NpcFunT_SetText(Type,Index,BtnID,SubID)
	elseif Type == NPC_FLAG_SYS_REPETITION then
		NpcFunR_SetText(Type,Index,BtnID,SubID)
	end
end
'''
SCRIPT = '''function NpcFunT_SetText(Type,Index,BtnID,SubID)
	if Index == 1 then
		FirstWin_Text1:SetText(NF_L0_001);
		if SubID== 1 then
			local Button = win:GetChild("FirstWin_Button" .. BtnID);
			Button:SetText(NF_L0_1);
		elseif SubID == 2 then
			local Button = win:GetChild("FirstWin_Button" .. BtnID);
			Button:SetText(NF_L0_2);
		else
			FirstWin_Text2:SetText("|cff00FF00"..NF_L0_1..a.."x");
		end
	elseif Index == 2 then
		Button:SetText(NF_L0_2);
	end
end
function NpcFunR_SetText(Type,Index,BtnID,SubID)
	if Index == 1 then
		if SubID == 15 then
			Button:SetText(LuaText["NF_EM_B15"]);
		end
	end
end
'''


def client_tree(root):
    loc = os.path.join(root, "Localization", "en_us")
    os.makedirs(os.path.join(loc, "UI", "Base"))
    os.makedirs(os.path.join(loc, "UI", "XML", "NpcFun"))
    with open(os.path.join(loc, "UI", "XML", "NpcFunLoad.xml"), "w", encoding="utf-8-sig") as f:
        f.write('<UIConfig>\r\n <Script File="./Localization/en_us/UI/Base/LuaText.lua" />\r\n'
                ' <Script File="./Localization/en_us/UI/XML/NpcFun/NpcFun.lua" />\r\n'
                ' <Script File="./Localization/en_us/UI/XML/NpcFun/NpcFunT.lua" />\r\n</UIConfig>\r\n')
    with open(os.path.join(loc, "UI", "Base", "LuaText.lua"), "wb") as f:
        f.write(b"\xef\xbb\xbf" + LUATEXT.replace("\n", "\r\n").encode())
    open(os.path.join(loc, "UI", "XML", "NpcFun", "NpcFun.lua"), "w").write(DISPATCH)
    open(os.path.join(loc, "UI", "XML", "NpcFun", "NpcFunT.lua"), "w").write(SCRIPT)
    return loc


def test_build_resolves_options_and_page_text():
    with tempfile.TemporaryDirectory() as d:
        client_tree(d)
        doc = B.build(d)
    t = doc["types"]["1"]
    assert t["fn"] == "NpcFunT_SetText" and t["const"] == "NPC_FLAG_SYS_TRANMIT"
    p1 = t["pages"]["1"]
    assert p1["1"]["buttons"][0]["text"] == "|cffFFFF00*Go to A|cFFFFFFFF"
    assert p1["1"]["buttons"][0]["keys"] == ["NF_L0_1"] and not p1["1"]["buttons"][0]["dynamic"]
    assert p1["2"]["buttons"][0]["keys"] == ["NF_L0_2"]
    assert p1["*"]["texts"][0]["text"] == "Where do you want to go?"          # outside the SubID chain = every option
    el = p1["else"]["texts"][0]                                                # "lit" .. KEY .. a .. "x"
    assert el["dynamic"] and "{a}" in el["text"] and el["keys"] == ["NF_L0_1"]
    assert t["pages"]["2"]["*"]["buttons"][0]["keys"] == ["NF_L0_2"]
    assert doc["types"]["9"]["pages"]["1"]["15"]["buttons"][0]["text"].endswith("Wonderland|cFFFFFFFF")   # LuaText["X"]
    assert doc["strings"]["NF_L0_2"]["text"] == "|cffFFFF00*Go to B|cFFFFFFFF"   # the comment line did not win
    assert "Msg" not in doc["strings"]


def test_page_join_follows_menu_order():
    with tempfile.TemporaryDirectory() as d:
        client_tree(d)
        doc = B.build(d)
    pg = MENU.page(doc, 1, [2, 1, 7])
    assert [o["sub"] for o in pg["options"]] == [2, 1, 7]
    assert pg["options"][0]["label"]["key"] == "NF_L0_2"
    assert pg["options"][2]["label"] is None                                    # 7 falls to else: page text only
    assert pg["texts"][0]["key"] == "NF_L0_001"
    missing = MENU.page(doc, 77, [1])
    assert not missing["resolved"] and missing["options"][0]["label"] is None


def test_check_label_and_lua_export():
    strings = {"NF_L0_1": {"text": "a", "file": "UI/Base/LuaText.lua", "line": 2}}
    assert MENU.check_label("NF_L0_1", "b", strings) == "b"
    for key, text in (("NOPE", "x"), ("NF_L0_1", ""), ("NF_L0_1", "a\nb"), ("NF_L0_1", "x" * 2000)):
        try:
            MENU.check_label(key, text, strings)
            raise AssertionError("accepted %r" % text)
        except Bad:
            pass
    lua = MENU.export_lua({"labels": {"NF_L0_1": 'say "hi" \\ there'}}, strings)
    assert 'NF_L0_1 = "say \\"hi\\" \\\\ there"' in lua


def test_apply_tool_edits_in_place_and_refuses_stale():
    with tempfile.TemporaryDirectory() as d:
        loc = client_tree(d)
        doc = B.build(d)
        path = os.path.join(loc, "UI", "Base", "LuaText.lua")
        files, errs = APPLY.plan({"NF_L0_2": '|cffFFFF00*Ride to "B"|cFFFFFFFF'}, doc["strings"], d)
        assert not errs
        for p, edits in files.items():
            done, errs = APPLY.apply_file(p, edits, False)
            assert done == ["NF_L0_2"] and not errs
        raw = open(path, "rb").read()
        assert raw.startswith(b"\xef\xbb\xbf") and b"\r\n" in raw                    # encoding and line ends kept
        assert b'NF_L0_2 = "|cffFFFF00*Ride to \\"B\\"|cFFFFFFFF"\r\n' in raw
        assert os.path.exists(path + ".orig")
        # the same draft again: the defining line no longer holds the original text -> refused
        files, _ = APPLY.plan({"NF_L0_2": "x"}, doc["strings"], d)
        for p, edits in files.items():
            done, errs = APPLY.apply_file(p, edits, False)
            assert not done and errs


def test_committed_data_covers_the_published_transporter():
    path = os.path.join(ED, "data", "npc_menu_text.json")
    doc = json.load(open(path, encoding="utf-8"))
    pg = MENU.page(doc, 1, [4, 6, 5, 7, 9, 10])            # athens_transporter in the reviewed release
    labels = [o["label"]["text"] for o in pg["options"]]
    assert all(l and "*Go to" in l for l in labels), labels
    assert "Marathon" in labels[1]


def test_server_added_transporter_buttons_follow_the_menu():
    doc = json.load(open(os.path.join(ED, "data", "npc_menu_text.json"), encoding="utf-8"))
    extra = MENU.extensions("Athens_041", MENU.TRANSPORTER_BEHAVIOUR)
    pg = MENU.page(doc, 1, [4, 6, 5, 7, 9, 10], extra=extra)
    subs = [o["sub"] for o in pg["options"]]
    assert subs == [4, 6, 5, 7, 9, 10, 11, 1089, 1088, 1091, 1090, 1092]
    assert [o["added"] for o in pg["options"]] == [False] * 6 + [True] * 6
    assert "Mycenae" in pg["options"][6]["label"]["text"]
    assert MENU.extensions("Athens_041", 15) == [] and MENU.extensions("Athens_055", 14) == []


def test_mirror_matches_server_source():
    src = os.path.join(ED, "..", "server", "src", "Godswar.Server", "Domain", "World", "Content", "TransporterProtocol.cs")
    if not os.path.exists(src):
        return
    cs = open(src, encoding="utf-8").read()
    import re
    consts = {m.group(1): int(m.group(2)) for m in re.finditer(r"const int (\w+MenuSubId) = (\d+);", cs)}
    def arr(name):
        body = re.search(name + r"\s*=\s*\[([^\]]*)\]", cs).group(1)
        return [consts[x.strip()] if x.strip() in consts else int(x) for x in body.split(",") if x.strip()]
    assert arr("CapitalExtensionMenuValues") == MENU.TRANSPORTER_EXTENSIONS["Athens_041"] == MENU.TRANSPORTER_EXTENSIONS["Sparta_042"]
    assert arr("MycenaeExtensionMenuValues") == MENU.TRANSPORTER_EXTENSIONS["Mycenae_All_013"]
