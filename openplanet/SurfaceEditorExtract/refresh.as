// Items written while the game runs: reload them from disk and refresh their catalog entries, so no restart is needed.
// The GUI writes refresh-request.txt (one path per line, relative to the user folder); results go to refresh-result.txt.
// Native calls and patterns adapted from Editor++ (XertroV, public domain).

namespace Refresh
{
    // NGameItemUtils_AddOrRefreshItemModelArticle(model): pattern inside the function, found at fn + 0x0B
    const string AddPat = "83 BB F0 00 00 00 0B 0F 94 C1 E8 ?? ?? ?? ?? 48 8B 53 08 44 8B C8 4C 8B 05 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? C7 44 24 28 00 00 00 00 C7 44 24 20 03 00 00 00 E8";
    const uint AddPatOff = 0x0B;
    const string AddPrologue = "40 53 48 83 EC 40 48 8B D9 33 C9";
    // its call in the item editor's after-save, the E8 at +0x07
    const string AddCallerPat = "48 8B 8B F8 08 00 00 E8 ?? ?? ?? ?? 48 8B 83 F8 08 00 00 48 85 C0 74 ?? 48 8B 78 08";
    const uint AddCallerE8 = 0x07;

    // CGameCtnEditorCommonInterface_RebuildArticleInventory(interface, kind): the map editor's tree from the catalog
    const string RebuildPat = "C7 44 24 40 00 00 00 00 48 8B F1 48 8D 54 24 40 48 8B 49 40 49 8B D8 E8 ?? ?? ?? ?? 48 8B 46 40 4C 8B C3 8B D7 48 8B CE 4C 8B 88 98 04 00 00";
    const uint RebuildPatOff = 0x14;
    const string RebuildCallerPat = "48 8B 8E 20 06 00 00 45 33 C0 41 8D 50 03 E8 ?? ?? ?? ?? 48 85 DB 74 ?? 4C 8B AE 48 11 00 00 4C 8B C3 49 8B D5";
    const uint RebuildCallerE8 = 0x0E;
    const uint KindItems = 3;

    // CSystemFid +0x80 = its nod, CMwNod +0x08 = its fid
    const uint FidNod = 0x80;
    const uint NodFid = 0x08;

    uint64 g_add = 0;
    uint64 g_rebuild = 0;

    uint64 CallTarget(uint64 e8)
    {
        return e8 + 5 + uint64(int64(Dev::ReadInt32(e8 + 1)));
    }

    // both anchors must give the same address, or the game changed and we don't call anything
    uint64 Resolve(const string &in name, const string &in pat, uint patOff, const string &in callerPat, uint callerE8, const string &in prologue, string &out err)
    {
        uint64 inside = Dev::FindPattern(pat);
        uint64 caller = Dev::FindPattern(callerPat);
        if (inside == 0 || caller == 0) { err = name + ": pattern not found (game updated?)"; return 0; }
        if (Dev::ReadUInt8(caller + callerE8) != 0xE8) { err = name + ": caller is not a call"; return 0; }
        uint64 fn = inside - patOff;
        if (fn != CallTarget(caller + callerE8)) { err = name + ": anchors disagree"; return 0; }
        if (prologue.Length > 0 && !Hex::Matches(fn, prologue)) { err = name + ": prologue differs"; return 0; }
        return fn;
    }

    // one item from disk into the catalog; an item the game holds already is unbound first, so Preload reads the new file
    bool Item(const string &in path, string &out err)
    {
        auto fid = Fids::GetUser(path);
        if (fid is null) { err = "no fid"; return false; }
        if (!IO::FileExists(IO::FromUserGameFolder(path.Replace("\\", "/")))) { err = "no file"; return false; }
        CMwNod@ old = fid.Nod;
        if (old !is null) {
            // the old model stays alive for whatever uses it (an open map); it just no longer owns the file
            Dev::SetOffset(old, NodFid, uint64(0));
            Dev::SetOffset(fid, FidNod, uint64(0));
        }
        auto model = cast<CGameItemModel>(Fids::Preload(fid));
        if (model is null) {
            if (old !is null) {
                Dev::SetOffset(fid, FidNod, old);
                Dev::SetOffset(old, NodFid, fid);
            }
            err = "not an item";
            return false;
        }
        AsCall::Invoke(g_add, AsCall::PointerOf(model), 0, 0, 0);
        return true;
    }

    void Run(const array<string> &in paths)
    {
        string err;
        if (g_add == 0) g_add = Resolve("AddOrRefreshItemModelArticle", AddPat, AddPatOff, AddCallerPat, AddCallerE8, AddPrologue, err);
        if (g_rebuild == 0) g_rebuild = Resolve("RebuildArticleInventory", RebuildPat, RebuildPatOff, RebuildCallerPat, RebuildCallerE8, "", err);
        if (err.Length == 0) err = AsCall::Ensure();
        array<string> lines;
        uint ok = 0;
        if (err.Length > 0) {
            lines.InsertLast("error " + err);
        } else {
            Fids::UpdateTree(Fids::GetUserFolder("Items"));
            uint64 started = Time::Now;
            for (uint i = 0; i < paths.Length; i++) {
                string why;
                bool done = false;
                try { done = Item(paths[i], why); } catch { why = getExceptionInfo(); }
                if (done) { ok++; lines.InsertLast("ok " + paths[i]); }
                else lines.InsertLast("failed " + paths[i] + " | " + why);
                if (Time::Now - started > 200) { yield(); started = Time::Now; }
            }
            auto editor = cast<CGameCtnEditorFree>(GetApp().Editor);
            if (ok > 0 && editor !is null && editor.EditorInterface !is null && g_rebuild != 0)
                AsCall::Invoke(g_rebuild, AsCall::PointerOf(editor.EditorInterface), KindItems, 0, 0);
        }
        IO::File f(PluginFile("refresh-result.txt"), IO::FileMode::Write);
        for (uint i = 0; i < lines.Length; i++) f.WriteLine(lines[i]);
        f.Close();
        print("refreshed " + ok + " of " + paths.Length + " items" + (err.Length > 0 ? ": " + err : ""));
    }

    void FromRequest()
    {
        string file = PluginFile("refresh-request.txt");
        IO::File f(file, IO::FileMode::Read);
        string text = f.ReadToEnd();
        f.Close();
        IO::Delete(file);
        array<string> paths;
        auto parts = text.Split("\n");
        for (uint i = 0; i < parts.Length; i++) {
            string p = parts[i].Trim();
            if (p.Length > 0) paths.InsertLast(p.Replace("/", "\\"));
        }
        Run(paths);
    }

}

namespace Hex
{
    array<uint8> Parse(const string &in hex)
    {
        array<uint8> bytes;
        auto parts = hex.Split(" ");
        for (uint i = 0; i < parts.Length; i++)
            if (parts[i].Length == 2) bytes.InsertLast(uint8(Text::ParseUInt(parts[i], 16)));
        return bytes;
    }

    void Write(uint64 at, const string &in hex)
    {
        auto b = Parse(hex);
        for (uint i = 0; i < b.Length; i++) Dev::Write(at + i, b[i]);
    }

    bool Matches(uint64 at, const string &in hex)
    {
        auto b = Parse(hex);
        for (uint i = 0; i < b.Length; i++) if (Dev::ReadUInt8(at + i) != b[i]) return false;
        return true;
    }
}

// calls a native function on Openplanet's thread: a button whose OnAction slot points at a stub that loads the arguments and calls
namespace AsCall
{
    const uint OnActionSlot = 0x200;
    const uint VtableBytes = 0x280;
    const uint OffFn = 0x80;
    const uint OffRcx = 0x88;
    const uint OffRdx = 0x90;
    const uint OffR8 = 0x98;
    const uint OffR9 = 0xA0;
    const uint OffRet = 0xA8;
    const uint OffThis = 0xD0;
    const uint StubSize = 0xE0;

    // 16 nops, aligned frame, save callee-saved, keep OnAction's this, call [OffFn](rcx, rdx, r8, r9), store rax, restore
    const string Code =
        "90 90 90 90 90 90 90 90 90 90 90 90 90 90 90 90 " +
        "55 48 89 E5 48 83 E4 F0 48 83 EC 40 53 56 57 41 54 41 55 41 56 41 57 48 83 EC 08 " +
        "48 89 0D 9E 00 00 00 " +
        "48 8B 05 47 00 00 00 48 8B 0D 48 00 00 00 48 8B 15 49 00 00 00 4C 8B 05 4A 00 00 00 4C 8B 0D 4B 00 00 00 " +
        "FF D0 48 89 05 4A 00 00 00 48 31 C0 48 8B 0D 68 00 00 00 " +
        "48 83 C4 08 41 5F 41 5E 41 5D 41 5C 5F 5E 5B 48 89 EC 5D C3";

    CControlButton@ g_carrier;
    uint64 g_origVt = 0;
    uint64 g_fakeVt = 0;
    uint64 g_stub = 0;
    uint64 g_scratch = 0;
    CMwNod@ g_scratchNod;

    string Ensure()
    {
        if (g_carrier !is null) return "";
        g_stub = Dev::Allocate(StubSize, true);
        if (g_stub == 0) return "no executable memory";
        for (uint i = 0; i < StubSize; i++) Dev::Write(g_stub + i, uint8(0x90));
        Hex::Write(g_stub, Code);
        @g_carrier = CControlButton();
        g_origVt = Dev::GetOffsetUint64(g_carrier, 0);
        g_fakeVt = Dev::Allocate(VtableBytes, false);
        for (uint i = 0; i < VtableBytes / 8; i++) Dev::Write(g_fakeVt + i * 8, Dev::ReadUInt64(g_origVt + i * 8));
        Dev::Write(g_fakeVt + OnActionSlot, g_stub);
        Dev::SetOffset(g_carrier, 0, g_fakeVt);
        return "";
    }

    uint64 PointerOf(CMwNod@ nod)
    {
        if (g_scratch == 0) {
            g_scratch = Dev::Allocate(0x1000, false);
            auto tmp = CMwNod();
            uint64 was = Dev::GetOffsetUint64(tmp, 0);
            Dev::SetOffset(tmp, 0, g_scratch);
            @g_scratchNod = Dev::GetOffsetNod(tmp, 0);
            Dev::SetOffset(tmp, 0, was);
        }
        Dev::SetOffset(g_scratchNod, 0, nod);
        return Dev::GetOffsetUint64(g_scratchNod, 0);
    }

    uint64 Invoke(uint64 fn, uint64 rcx, uint64 rdx, uint64 r8, uint64 r9)
    {
        if (fn == 0) throw("no function");
        Dev::Write(g_stub + OffFn, fn);
        Dev::Write(g_stub + OffRcx, rcx);
        Dev::Write(g_stub + OffRdx, rdx);
        Dev::Write(g_stub + OffR8, r8);
        Dev::Write(g_stub + OffR9, r9);
        Dev::Write(g_stub + OffRet, uint64(0));
        Dev::Write(g_stub + OffThis, PointerOf(g_carrier));
        g_carrier.OnAction();
        return Dev::ReadUInt64(g_stub + OffRet);
    }

    void Drop()
    {
        if (g_carrier !is null && g_origVt != 0) Dev::SetOffset(g_carrier, 0, g_origVt);
        @g_carrier = null;
        if (g_fakeVt != 0) Dev::Free(g_fakeVt);
        if (g_stub != 0) Dev::Free(g_stub);
        g_fakeVt = 0;
        g_stub = 0;
    }
}

void OnDestroyed() { AsCall::Drop(); }
