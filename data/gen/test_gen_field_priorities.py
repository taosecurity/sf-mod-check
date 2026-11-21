"""Self-test for gen_field_priorities.py. Builds a tiny made-up xEdit source tree and Mutagen XML in a temp
folder (no game data, no real xEdit or Mutagen files) and checks the priorities and the name mapping.

Run: python -I data/gen/test_gen_field_priorities.py
"""
import json
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gen_field_priorities as g  # noqa: E402

IFACE = """unit wbInterface;
interface
type
  TwbConflictPriority = (cpIgnore, cpBenignIfAdded, cpBenign, cpOverride, cpTranslate, cpNormal,
                         cpNormalIgnoreEmpty, cpCritical, cpFormID);
function wbString(const aSignature: TwbSignature; const aName: string; aSize: Integer = 0; aPriority: TwbConflictPriority = cpNormal): IwbSubRecordDef; overload;
function wbInteger(const aName: string; aType: TwbIntType; aFormater: IwbIntegerDefFormater = nil; aPriority: TwbConflictPriority = cpNormal): IwbIntegerDef; overload;
function wbFloat(const aName: string; aPriority: TwbConflictPriority = cpNormal): IwbFloatDef; overload;
function wbStruct(const aSignature: TwbSignature; const aName: string; const aMembers: array of IwbValueDef; aPriority: TwbConflictPriority = cpNormal): IwbSubRecordDef; overload;
function wbRStruct(const aName: string; const aMembers: array of IwbRecordMemberDef; const aSkip: array of TwbSignature): IwbSubRecordStructDef; overload;
function wbFormIDCk(const aSignature: TwbSignature; const aName: string; const aValid: array of TwbSignature; aPersistent: Boolean = False; aPriority: TwbConflictPriority = cpNormal): IwbSubRecordDef; overload;
function wbUnused(aSize: Integer): IwbValueDef; overload;
function wbEnum(const aNames: array of string): IwbEnumDef;
implementation
end.
"""

SIGS = """unit wbDefinitionsSignatures;
interface
const
""" + "".join(f"  {s} : TwbSignature = '{s}';\n" for s in
              ["EDID", "FULL", "DATA", "DNAM", "XOWN", "XRNK", "BFCB", "BFCE", "ANAM", "BNAM", "CNAM", "ENAM", "FNAM",
               "GNAM", "HNAM", "INAM", "JNAM", "KNAM", "LNAM", "MNAM", "NNAM", "ONAM", "PNAM", "QNAM", "RNAM", "SNAM",
               "TNAM", "UNAM", "VNAM", "WNAM", "XNAM", "YNAM", "ZNAM", "LNA2", "TSTR", "TREF", "KYWD", "NPC_"]) + \
    "implementation\nend.\n"

COMMON = """unit wbDefinitionsCommon;
interface
implementation
function wbOwnerHelper(aRank: Boolean = True): IwbRecordMemberDef;
begin
  Result := wbRStruct('Ownership', [
      IsFO4Plus(wbStruct(XOWN, 'Owner', [wbFormIDCk('Owner', [NPC_]), wbUnused(4)]), wbString(XOWN, 'Old Owner')),
      wbStruct(XRNK, 'Rank', [wbInteger('Rank', itS32)])
    ], []);
end;
end.
"""

# Made-up record definitions in xEdit's style: one test per rule.
SF1 = """unit wbDefinitionsSF1;
interface
implementation
procedure TwbGameDefSF1.Define;
begin
  var wbEDID := wbString(EDID, 'Editor ID', 0, cpOverride) as IwbSubRecordDef;
  var wbNamed := function(aSig: TwbSignature; aName: string = 'Default Name'): IwbSubRecordDef
  begin
    Result := wbString(aSig, aName, 0, cpTranslate);
  end;
  var wbBig := wbRStruct('Components', [
      wbString(BFCB, 'Start'),
      wbString(ANAM, 'A'), wbString(BNAM, 'B'), wbString(CNAM, 'C'), wbString(DNAM, 'D'), wbString(ENAM, 'E'),
      wbString(FNAM, 'F'), wbString(GNAM, 'G'), wbString(HNAM, 'H'), wbString(INAM, 'I'), wbString(JNAM, 'J'),
      wbString(KNAM, 'K'), wbString(MNAM, 'M'), wbString(NNAM, 'N'), wbString(ONAM, 'O'), wbString(PNAM, 'P'),
      wbString(QNAM, 'Q'), wbString(RNAM, 'R'), wbString(SNAM, 'S'), wbString(TNAM, 'T'), wbString(UNAM, 'U'),
      wbString(BFCE, 'End')
    ], []);
  var TestRef :=
    procedure(aSignature: TwbSignature; const aName: string)
    begin
      RegisterRefRecordDef(aSignature, aName, [
        wbEDID,
        wbFormIDCk(TREF, 'Location Reference', [KYWD], False, cpBenignIfAdded)
      ]);
    end;

  RegisterRecordDef(TSTR, 'Test Record', [
    wbEDID,
    wbNamed(FULL),
    wbStruct(DATA, 'Data', [
      wbFloat('Speed'),
      wbInteger('Unused', itU8),
      wbInteger('Count', itU32, nil, cpBenign),
      wbInteger('Mode', itU8, wbEnum(['A', 'B']))
    ]),
    wbStruct(DNAM, 'All Ignored', [
      wbUnused(4),
      wbInteger('Pad', itU8, nil, cpIgnore)
    ]),
    wbString(ZNAM, 'Critical Thing', 0, cpCritical).SetGetCP(wbSomeGetCP),
    wbOwnerHelper,
    wbString(LNA2, 'Navmesh Link'),
    wbBig
  ]);
  TestRef(TREF, 'Placed Test');
end;
end.
"""

MUTAGEN = """<?xml version="1.0" encoding="us-ascii"?>
<Loqui xmlns="http://tempuri.org/LoquiSource.xsd">
  <Object name="TestRecord" recordType="TSTR" baseClass="StarfieldMajorRecord" objType="Record">
    <Fields>
      <String name="Name" recordType="FULL" translated="Normal" />
      <Data recordType="DATA">
        <Fields>
          <Float name="Speed" />
          <UInt8 name="Unused" />
          <UInt32 name="Count" />
          <UInt8 name="Mode" />
        </Fields>
      </Data>
      <ByteArray name="DNAM" recordType="DNAM" />
      <String name="CriticalThing" recordType="ZNAM" />
      <RefDirect name="Ownership" refName="Ownership" />
      <FormLink name="NavmeshLink" recordType="LNA2" refName="TestRecord" />
      <RefList name="Components" refName="AComponent" />
      <String name="Missing" recordType="YNAM" />
    </Fields>
  </Object>
  <Object name="Ownership" objType="Subrecord">
    <Fields>
      <Data recordType="XOWN"><Fields><FormLink name="Owner" /></Fields></Data>
      <Int32 name="Rank" recordType="XRNK" />
    </Fields>
  </Object>
  <Object name="AComponent" objType="Subrecord" abstract="true">
    <Fields><CustomLogic name="Start" recordType="BFCB" /></Fields>
  </Object>
  <Object name="SomeComponent" objType="Subrecord" baseClass="AComponent">
    <Fields><String name="A" recordType="ANAM" /></Fields>
  </Object>
  <Object name="PlacedTest" recordType="TREF" baseClass="StarfieldMajorRecord" objType="Record">
    <Fields><FormLink name="Location" recordType="TREF" refName="TestRecord" /></Fields>
  </Object>
</Loqui>
"""

FAILS = []


def check(cond, msg):
    if not cond:
        FAILS.append(msg)
        print("FAIL:", msg)


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)


def main():
    with tempfile.TemporaryDirectory(prefix="test-fieldprio-") as tmp:
        core = os.path.join(tmp, "Core")
        write(os.path.join(core, "wbInterface.pas"), IFACE)
        write(os.path.join(core, "wbDefinitionsSignatures.pas"), SIGS)
        write(os.path.join(core, "wbDefinitionsCommon.pas"), COMMON)
        write(os.path.join(core, "wbDefinitionsSF1.pas"), SF1)
        recs = os.path.join(tmp, "Records")
        write(os.path.join(recs, "Major Records", "TestRecord.xml"), MUTAGEN)
        dll = os.path.join(tmp, "fake.dll")
        with open(dll, "wb") as fh:   # a stand-in DLL: just the names the "0.54.4" build would contain
            fh.write(b"\0" + b"\0".join(b"get_" + n.encode() for n in
                                        ["Name", "Speed", "Unused", "Count", "Mode", "DNAM", "CriticalThing",
                                         "Ownership", "NavmeshLink", "Components", "Location"]) + b"\0")
        out = os.path.join(tmp, "out.json")
        txt = os.path.join(tmp, "field-priorities.txt")
        rc = g.main(["--xedit-core", core, "--mutagen-records", recs, "--mutagen-dll", dll, "--out", txt,
                     "--json", out, "--log", os.path.join(tmp, "log.txt")])
        check(rc == 0, f"main returned {rc}")
        with open(out, encoding="utf-8") as fh:
            d = json.load(fh)
        with open(txt, encoding="utf-8") as fh:
            table = fh.read().splitlines()

        check(d["credit"] == g.CREDIT and d["license"] == "MPL-2.0", "credit line and license")
        check(d["default"]["cp"] == "cpNormal", "default is cpNormal")
        check(set(d["records"]) == {"TSTR", "TREF"}, f"records found: {sorted(d['records'])}")
        r = d["records"]["TSTR"]
        f = {e["xedit"]: e for e in r["fields"]}
        m = r["by_mutagen"]

        check(f["Editor ID"]["cp"] == "cpOverride" and f["Editor ID"]["priority"] == "benign", "'as' cast keeps EDID cpOverride")
        check(m["EditorID"]["priority"] == "benign", "EditorID mapped")
        check(f["Default Name"]["cp"] == "cpTranslate", "helper with a default parameter")
        check(m["Name"]["priority"] == "normal", "cpTranslate counts as normal")
        data = f["Data"]
        check(data["leaf_cps"] == {"cpIgnore": 1, "cpBenign": 1, "cpNormal": 2}, f"Data leaves: {data['leaf_cps']}")
        check(data["priority"] == "normal" and "leaves" in data, "mixed member keeps the highest live priority and lists the rest")
        check(all(m[n]["sig"] == ["DATA"] for n in ("Speed", "Unused", "Count", "Mode")), "Data block fields map to DATA")
        check(f["All Ignored"]["priority"] == "ignore", "a member whose leaves are all ignored is ignore")
        check(m["DNAM"]["priority"] == "ignore", "DNAM mapped as ignore")
        crit = f["Critical Thing"]
        check(crit["priority"] == "critical" and crit.get("dynamic") == ["wbSomeGetCP"], "cpCritical and SetGetCP noted")
        own = f["Ownership"]
        check(own["sig"] == ["XOWN", "XRNK"], f"game selector picks the Starfield branch: {own['sig']}")
        check(own["leaf_cps"].get("cpIgnore") == 1, "wbUnused inside the Starfield branch is cpIgnore")
        check(m["Ownership"]["xedit"] == "Ownership", "RefDirect follows the embedded object's subrecords")
        check(m["NavmeshLink"]["xedit"] == "Navmesh Link", "a FormLink's refName is not followed")
        check(m["Components"]["xedit"] == "Components", "the component block takes the field that opens with BFCB")
        check(m["Missing"].get("unmapped") and m["Missing"]["priority"] == "normal", "unmapped Mutagen field gets the default")
        check(m["Missing"].get("dll_check"), "a name missing from the DLL is flagged")
        check(not m["Speed"].get("dll_check"), "a name present in the DLL is not flagged")
        t = d["records"]["TREF"]
        check(t["by_mutagen"]["Location"].get("benign_if_added") and t["by_mutagen"]["Location"]["cp"] == "cpBenignIfAdded",
              "registration inside a helper procedure, cpBenignIfAdded flagged")
        check(d["mutagen_types"] == {"PlacedTest": "TREF", "TestRecord": "TSTR"}, f"type index {d['mutagen_types']}")

        # the trimmed table
        rows = [ln for ln in table if not ln.startswith("#")]
        heads = [ln for ln in table if ln.startswith("#")]
        check(any(g.CREDIT in ln for ln in heads), "table header carries the credit line")
        check(any("Mozilla Public License, v. 2.0" in ln for ln in heads), "table header carries the MPL-2.0 notice")
        check(any("b0e67b16" in ln for ln in heads), "table header names the xEdit commit")
        check("type TestRecord TSTR" in rows and "type PlacedTest TREF" in rows, "type lines map classes to records")
        check("header FormKey critical" in rows and "header FormVersion ignore" in rows, "record header lines")
        check("TSTR EditorID benign" in rows, "EditorID row")
        check("TSTR DNAM ignore" in rows, "ignore row")
        check("TSTR CriticalThing critical dynamic=wbSomeGetCP" in rows, "critical row with its dynamic flag")
        check("TREF Location normal benign-if-added" in rows, "benign-if-added flag")
        check(not any(r.startswith("TSTR Missing ") for r in rows), "a field with no xEdit member is left out (default normal)")
        check(all(len(r.split()) >= 3 for r in rows), "every row has at least three columns")
        # a second run gives the same bytes
        txt2 = os.path.join(tmp, "again.txt")
        g.main(["--xedit-core", core, "--mutagen-records", recs, "--mutagen-dll", dll, "--out", txt2])
        with open(txt, "rb") as f1, open(txt2, "rb") as f2:
            check(f1.read() == f2.read(), "same table on a second run")

        # missing input names the file
        rc = g.main(["--xedit-core", os.path.join(tmp, "nowhere"), "--mutagen-records", recs, "--out", out])
        check(rc == 1, "missing xEdit source returns 1")
        # no defaults point at anyone's folders: both sources must be given
        rc = g.main(["--mutagen-records", recs, "--out", out])
        check(rc == 2, "no --xedit-core returns 2")
        rc = g.main(["--xedit-core", core, "--out", out])
        check(rc == 2, "no Mutagen source returns 2")

    print("test_gen_field_priorities:", "FAILED" if FAILS else "all checks passed")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
