"""Build data/field-priorities.txt: xEdit's Starfield field conflict priorities, keyed by Mutagen field names.

What it does
    xEdit (TES5Edit, MPL-2.0) gives every field of every record a conflict priority (cpIgnore, cpBenign,
    cpNormal, cpCritical and a few more; wbInterface.pas, TwbConflictPriority). This script reads xEdit's
    Starfield definitions (wbDefinitionsSF1.pas, with the shared helpers in wbDefinitionsCommon.pas), works
    out each record type's top-level members, their subrecord signatures and the priorities of the values
    inside them, and maps each member to the top-level property names of Mutagen's Starfield records, which
    is what sf-mod-check's `conflict` compares. Mutagen names come from Mutagen's own record definitions (the
    Loqui XML files in a Mutagen source tree) and are checked against the names in the
    Mutagen.Bethesda.Starfield 0.54.4 DLL when it is found.

    It only reads source files. It writes the table (and, with --json, the full view; with --log, a log).
    It never reads game data, and the table holds no xEdit code: priorities as data only.

Usage
    python -I gen_field_priorities.py --xedit-core <xEdit source>/Core
        (--mutagen-repo <Mutagen clone> [--mutagen-commit SHA] | --mutagen-records <Records folder>)
        [--mutagen-dll FILE] [--out FILE] [--json FILE] [--log FILE] [--verbose]

    xEdit source: https://github.com/TES5Edit/TES5Edit (the table describes commit b0e67b16, release 4.1.5r).
    Mutagen: https://github.com/Mutagen-Modding/Mutagen (default commit 0188012c, the 0.54.4 package's).

Exit codes: 0 = written, 1 = an input is missing or the parse found no records (the message names the file),
2 = bad options.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import subprocess
import sys
import tarfile
import tempfile
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field

HERE = os.path.dirname(os.path.abspath(__file__))
# the commit the Mutagen.Bethesda.Starfield 0.54.4 package was built from (its .nuspec <repository commit=...>)
DEF_MUTAGEN_COMMIT = "0188012c607ce8bb283d2704400d37737f089134"
MUTAGEN_SUBDIR = "Mutagen.Bethesda.Starfield/Records"
DEF_DLL = os.path.join(os.path.expanduser("~"), ".nuget", "packages", "mutagen.bethesda.starfield", "0.54.4",
                       "lib", "net9.0", "Mutagen.Bethesda.Starfield.dll")
DEF_OUT = os.path.normpath(os.path.join(HERE, "..", "field-priorities.txt"))
DEF_XEDIT_COMMIT = "b0e67b16"
DEF_XEDIT_RELEASE = "4.1.5r"

CREDIT = "Field conflict priorities derived from xEdit (https://github.com/TES5Edit/TES5Edit), MPL-2.0"

# xEdit priority -> our four buckets. Order of severity for "the highest priority inside a field".
BUCKET = {
    "cpIgnore": "ignore",
    "cpBenignIfAdded": "normal",   # benign only when the earlier copy lacks the value (flagged per field)
    "cpBenign": "benign",
    "cpOverride": "benign",        # xEdit shows "Override without conflict"
    "cpTranslate": "normal",       # only special in xEdit's translation mode
    "cpNormal": "normal",
    "cpNormalIgnoreEmpty": "normal",  # a copy without the value is ignored
    "cpCritical": "critical",
    "cpFormID": "critical",        # record header FormID; benign in GMST and DFOB
}
SEVERITY = {"ignore": 0, "benign": 1, "normal": 2, "critical": 3}
CP_ORDER = ["cpIgnore", "cpBenignIfAdded", "cpBenign", "cpOverride", "cpTranslate", "cpNormal",
            "cpNormalIgnoreEmpty", "cpCritical", "cpFormID"]

LOG: list[str] = []
VERBOSE = False


def log(msg: str) -> None:
    LOG.append(msg)
    if VERBOSE:
        print(msg, file=sys.stderr)


# ----------------------------------------------------------------------------------------------- tokenizer
@dataclass
class Tok:
    kind: str   # id, str, num, op
    val: str
    line: int
    pos: int    # index in the token list
    file: str


def tokenize(text: str, fname: str) -> list[Tok]:
    toks: list[Tok] = []
    i, n, line = 0, len(text), 1
    while i < n:
        c = text[i]
        if c == "\n":
            line += 1; i += 1; continue
        if c in " \t\r\f":
            i += 1; continue
        if text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j
            continue
        if c == "{":
            j = text.find("}", i)
            j = n if j < 0 else j
            line += text.count("\n", i, j); i = j + 1; continue
        if text.startswith("(*", i):
            j = text.find("*)", i)
            j = n if j < 0 else j
            line += text.count("\n", i, j); i = j + 2; continue
        if c == "'" or c == "#":
            # Pascal string: '..' pieces joined with #nn char codes
            s, j = [], i
            while j < n and (text[j] == "'" or text[j] == "#"):
                if text[j] == "'":
                    j += 1
                    while j < n:
                        if text[j] == "'":
                            if j + 1 < n and text[j + 1] == "'":
                                s.append("'"); j += 2; continue
                            j += 1; break
                        if text[j] == "\n":
                            line += 1
                        s.append(text[j]); j += 1
                else:
                    m = re.match(r"#(\$[0-9A-Fa-f]+|\d+)", text[j:])
                    if not m:
                        break
                    v = m.group(1)
                    s.append(chr(int(v[1:], 16) if v.startswith("$") else int(v)))
                    j += len(m.group(0))
            toks.append(Tok("str", "".join(s), line, len(toks), fname)); i = j; continue
        if c.isalpha() or c == "_" or c == "&":
            m = re.match(r"&?[A-Za-z_][A-Za-z0-9_]*", text[i:])
            toks.append(Tok("id", m.group(0).lstrip("&"), line, len(toks), fname)); i += len(m.group(0)); continue
        if c.isdigit() or (c == "$" and i + 1 < n and text[i + 1] in "0123456789abcdefABCDEF"):
            m = re.match(r"\$[0-9A-Fa-f]+|\d+(\.\d+(?!\.))?([eE][+-]?\d+)?", text[i:])
            toks.append(Tok("num", m.group(0), line, len(toks), fname)); i += len(m.group(0)); continue
        two = text[i:i + 2]
        if two in (":=", "<>", "<=", ">=", ".."):
            toks.append(Tok("op", two, line, len(toks), fname)); i += 2; continue
        toks.append(Tok("op", c, line, len(toks), fname)); i += 1
    return toks


# ----------------------------------------------------------------------------------------------- AST
@dataclass
class Node:
    k: str                 # lit, name, call, member, list, bin, unary, alt, anon, index
    v: object = None
    a: list = field(default_factory=list)
    line: int = 0
    pos: int = 0
    file: str = ""


@dataclass
class Func:
    name: str
    params: list           # [(name, default Node|None, type str)]
    body: tuple            # (start, end) token indices, body inclusive
    file: str
    toks: list
    line: int
    is_proc: bool


# TwbGameDef.Is*(aDef1, aDef2) in wbInterface.pas: does the test hold for Starfield?
GAME_SELECTORS = {"issf1": True, "isfo4plus": True, "isfo76sf1": True, "iscs": False, "isfnv": False,
                  "isfo3": False, "isfo76": False, "ishnvse": False, "issse": False, "istes3": False,
                  "istes4": False, "istes4fo3": False, "istes4r": False, "istes5": False}

BINOPS = {"or", "xor", "and", "=", "<>", "<", ">", "<=", ">=", "in", "is", "as", "+", "-", "*", "/",
          "div", "mod", "shl", "shr"}
BLOCK_OPEN = {"begin", "case", "try", "asm", "record"}


class Parser:
    def __init__(self, toks: list[Tok]):
        self.t = toks
        self.i = 0

    def peek(self, o=0):
        j = self.i + o
        return self.t[j] if j < len(self.t) else Tok("eof", "", 0, j, "")

    def lv(self, o=0):
        return self.peek(o).val.lower() if self.peek(o).kind == "id" else self.peek(o).val

    def take(self):
        t = self.peek(); self.i += 1; return t

    def expect(self, v):
        if self.lv() == v:
            self.i += 1
            return True
        return False

    def expr(self) -> Node:
        first = self.unary()
        ops = [first]
        while self.peek().kind in ("op", "id") and self.lv() in BINOPS:
            op = self.take().val.lower()
            ops.append(Node("lit", op))
            ops.append(self.unary())
        if len(ops) == 1:
            return first
        return Node("bin", None, ops[::2], first.line, first.pos, first.file)

    def unary(self) -> Node:
        if self.lv() in ("not", "-", "+", "@"):
            self.take()
            return Node("unary", None, [self.unary()])
        return self.postfix(self.primary())

    def primary(self) -> Node:
        t = self.peek()
        if t.kind == "str":
            self.take(); return Node("lit", t.val, [], t.line, t.pos, t.file)
        if t.kind == "num":   # numbers never name a field; keep them out of the name search
            self.take(); return Node("num", t.val, [], t.line, t.pos, t.file)
        if t.val == "[":
            self.take()
            items = []
            while self.peek().kind != "eof" and self.peek().val != "]":
                items.append(self.expr())
                if self.lv() == "..":
                    self.take(); self.expr()
                if not self.expect(","):
                    break
            self.expect("]")
            return Node("list", None, items, t.line, t.pos, t.file)
        if t.val == "(":
            self.take()
            e = self.expr()
            while self.expect(","):   # (a, b) only appears in odd places; keep the first
                self.expr()
            self.expect(")")
            return e
        if t.kind == "id" and t.val.lower() == "if":
            self.take()
            self.expr()
            self.expect("then")
            a = self.expr()
            b = self.expr() if self.expect("else") else Node("lit", None)
            return Node("alt", None, [a, b], t.line, t.pos, t.file)
        if t.kind == "id" and t.val.lower() in ("function", "procedure"):
            return self.anon()
        if t.kind == "id":
            self.take(); return Node("name", t.val, [], t.line, t.pos, t.file)
        self.take()
        return Node("lit", None, [], t.line, t.pos, t.file)

    def postfix(self, n: Node) -> Node:
        while True:
            v = self.peek().val
            if v == "(":
                self.take()
                args = []
                while self.peek().kind != "eof" and self.peek().val != ")":
                    args.append(self.expr())
                    if not self.expect(","):
                        break
                self.expect(")")
                n = Node("call", n, args, n.line, n.pos, n.file)
            elif v == "." and self.peek(1).kind == "id":
                self.take(); m = self.take()
                n = Node("member", m.val, [n], m.line, m.pos, m.file)
            elif v == "[" and n.k in ("name", "member", "call"):
                self.take()
                idx = []
                while self.peek().kind != "eof" and self.peek().val != "]":
                    idx.append(self.expr())
                    if not self.expect(","):
                        break
                self.expect("]")
                n = Node("index", None, [n] + idx, n.line, n.pos, n.file)
            elif v == "^":
                self.take()
            else:
                return n

    def params(self) -> list:
        """Parse '( const a, b: T = x; var c: T )' after the opening paren was seen."""
        out = []
        if not self.expect("("):
            return out
        while self.peek().kind != "eof" and self.peek().val != ")":
            while self.lv() in ("const", "var", "out", "constref"):
                self.take()
            names = [self.take().val]
            while self.expect(","):
                names.append(self.take().val)
            typ = ""
            if self.expect(":"):
                parts = []
                while self.peek().kind != "eof" and self.peek().val not in (";", ")", "="):
                    parts.append(self.take().val)
                typ = " ".join(parts)
            default = None
            if self.expect("="):
                default = self.expr()
            for nm in names:
                out.append((nm, default, typ))
            if not self.expect(";"):
                break
        self.expect(")")
        return out

    def skip_type(self):
        if self.expect(":"):
            while self.peek().kind != "eof" and self.peek().val not in (";",) and self.lv() not in ("begin", "var", "const"):
                self.take()

    def block_end(self, start: int) -> int:
        """start = index of a 'begin'; return index of its matching 'end'."""
        depth = 0
        j = start
        while j < len(self.t):
            t = self.t[j]
            if t.kind == "id":
                lv = t.val.lower()
                if lv in BLOCK_OPEN:
                    depth += 1
                elif lv == "end":
                    depth -= 1
                    if depth == 0:
                        return j
            j += 1
        return len(self.t) - 1

    def anon(self) -> Node:
        t = self.take()
        is_proc = t.val.lower() == "procedure"
        params = self.params() if self.peek().val == "(" else []
        self.skip_type()
        # local var sections before begin are rare in anonymous methods; skip to begin
        while self.peek().kind != "eof" and self.lv() != "begin":
            self.take()
        b = self.i
        e = self.block_end(b)
        self.i = e + 1
        f = Func("<anon>", params, (b, e), t.file, self.t, t.line, is_proc)
        return Node("anon", f, [], t.line, t.pos, t.file)


# ----------------------------------------------------------------------------------------------- values
@dataclass
class Def:
    ctor: str
    sig: list
    name: str
    cp: str
    children: list
    getcp: list
    line: int
    file: str


@dataclass
class Sig:
    s: str


@dataclass
class CP:
    c: str


@dataclass
class Alt:
    vals: list


@dataclass
class LitV:
    v: object


@dataclass
class ListV:
    items: list


OPAQUE = LitV(None)


class Source:
    def __init__(self, files: dict[str, str], signatures: set[str], ctor_types: dict[str, str]):
        self.toks: dict[str, list[Tok]] = {f: tokenize(t, f) for f, t in files.items()}
        self.sigs = signatures
        self.ctor_types = ctor_types
        self.funcs: dict[str, list[Func]] = {}
        self.binds: dict[str, list] = {}        # lower name -> [(file, pos, Node, else_flag)]
        self.bind_cache: dict[tuple, object] = {}
        self.records: list[dict] = []
        self.unresolved: dict[str, int] = {}
        self.helper_ranges: list[tuple] = []    # (file, start, end) of bodies with parameters
        self.stack: list[str] = []
        self.env_stack: list[dict] = []
        for f, toks in self.toks.items():
            self.scan(f, toks)

    # -- pass 1: functions, bindings
    def scan(self, f: str, toks: list[Tok]):
        p = Parser(toks)
        impl = 0
        for t in toks:
            if t.kind == "id" and t.val.lower() == "implementation":
                impl = t.pos
                break
        i = impl
        while i < len(toks):
            t = toks[i]
            lv = t.val.lower() if t.kind == "id" else None
            prev = toks[i - 1].val.lower() if i > 0 else ";"
            # named function / procedure (not anonymous)
            if lv in ("function", "procedure") and prev not in (":=", "(", ",", "="):
                p.i = i + 1
                parts = [p.take().val]
                while p.peek().val == "." and p.peek(1).kind == "id":
                    p.take(); parts.append(p.take().val)
                name = parts[-1]
                params = p.params() if p.peek().val == "(" else []
                p.skip_type()
                p.expect(";")
                while p.lv() in ("overload", "inline", "static", "virtual", "override", "reintroduce", "stdcall",
                                 "register", "cdecl"):
                    p.take(); p.expect(";")
                if p.lv() == "forward":
                    i = p.i; continue
                # find the own body 'begin': nested headers come with their own begin..end
                j = p.i
                pending = 0
                body = None
                while j < len(toks):
                    tj = toks[j]
                    lj = tj.val.lower() if tj.kind == "id" else ""
                    pj = toks[j - 1].val.lower()
                    if lj in ("function", "procedure") and pj not in (":=", "(", ",", "="):
                        pending += 1
                    elif lj == "begin":
                        e = p.block_end(j)
                        if pending:
                            pending -= 1
                            j = e + 1
                            continue
                        body = (j, e)
                        break
                    elif lj in ("implementation", "initialization") or (lj == "end" and toks[j + 1].val == "."):
                        break
                    j += 1
                if body:
                    fn = Func(name, params, body, f, toks, t.line, lv == "procedure")
                    self.funcs.setdefault(name.lower(), []).append(fn)
                    if params:
                        self.helper_ranges.append((f, body[0], body[1]))
                    # continue scanning inside the body (bindings live there)
                    i = body[0] + 1
                    continue
                i = p.i
                continue
            # var NAME [: T] := EXPR
            if lv == "var" and i + 1 < len(toks) and toks[i + 1].kind == "id":
                j = i + 2
                if toks[j].val == ":":
                    while j < len(toks) and toks[j].val not in (":=", ";"):
                        j += 1
                if j < len(toks) and toks[j].val == ":=":
                    p.i = j + 1
                    node = p.expr()
                    self.add_bind(toks[i + 1].val, f, toks[i + 1].pos, node, False)
                    i = p.i
                    continue
            # NAME := EXPR  or  NAME[idx] := EXPR at statement start
            if t.kind == "id" and prev in (";", "begin", "then", "else", "do") and lv not in ("result",):
                j = i + 1
                if j < len(toks) and toks[j].val == "[":
                    depth = 0
                    while j < len(toks):
                        if toks[j].val == "[":
                            depth += 1
                        elif toks[j].val == "]":
                            depth -= 1
                            if depth == 0:
                                break
                        j += 1
                    j += 1
                if j < len(toks) and toks[j].val == ":=":
                    p.i = j + 1
                    node = p.expr()
                    self.add_bind(t.val, f, t.pos, node, prev == "else")
                    i = p.i
                    continue
            i += 1

    def add_bind(self, name, f, pos, node, else_flag):
        if node.k == "anon":
            fn = node.v
            fn.name = name
            self.funcs.setdefault(name.lower(), []).append(fn)
            if fn.params:
                self.helper_ranges.append((f, fn.body[0], fn.body[1]))
            return
        self.binds.setdefault(name.lower(), []).append((f, pos, node, else_flag))

    def in_helper(self, f, pos):
        return any(hf == f and a <= pos <= b for hf, a, b in self.helper_ranges)

    # -- pass 2: record registrations
    def find_records(self, main_file: str):
        toks = self.toks[main_file]
        p = Parser(toks)
        for t in toks:
            if t.kind != "id":
                continue
            prev = toks[t.pos - 1].val.lower() if t.pos else ";"
            if prev not in (";", "begin", "then", "else", "do"):
                continue
            lv = t.val.lower()
            if lv in ("registerrecorddef", "registerrefrecorddef") or self.is_register_helper(lv):
                if self.in_helper(main_file, t.pos):
                    continue
                if toks[t.pos + 1].val != "(":
                    continue
                p.i = t.pos
                node = p.expr()
                self.ev(node)

    def is_register_helper(self, lv):
        for fn in self.funcs.get(lv, []):
            if fn.params and any(tt.kind == "id" and tt.val.lower() in ("registerrecorddef", "registerrefrecorddef")
                                 for tt in fn.toks[fn.body[0]:fn.body[1]]):
                return True
        return False

    # -- evaluation
    def env_lookup(self, lname):
        for env in reversed(self.env_stack[-1:]):
            if lname in env:
                return True, env[lname]
        return False, None

    def bind_value(self, lname, ref: Node):
        alts = self.binds.get(lname)
        if not alts:
            return None
        same = [a for a in alts if a[0] == ref.file and a[1] < ref.pos]
        if same:
            chosen = [same[-1]]
            if same[-1][3] and len(same) > 1:
                chosen.append(same[-2])
        else:
            other = [a for a in alts if a[0] != ref.file]
            chosen = other if other else [alts[-1]]
        vals = []
        for (f, pos, node, _e) in chosen:
            key = (f, pos)
            if key in self.bind_cache:
                v = self.bind_cache[key]
                if v is not None:
                    vals.append(v)
                continue
            self.bind_cache[key] = None   # cycle guard
            saved = self.env_stack
            self.env_stack = []
            v = self.ev(node)
            self.env_stack = saved
            self.bind_cache[key] = v
            vals.append(v)
        if not vals:
            return OPAQUE
        return vals[0] if len(vals) == 1 else Alt(vals)

    def resolve_name(self, n: Node):
        name = n.v
        lname = name.lower()
        found, v = self.env_lookup(lname)
        if found:
            return v
        if lname.startswith("cp") and lname in {c.lower() for c in CP_ORDER}:
            return CP(next(c for c in CP_ORDER if c.lower() == lname))
        if name in self.sigs:
            return Sig(name)
        bv = self.bind_value(lname, n)
        if bv is not None:
            return bv
        if lname in self.funcs:
            # a parameterless function used without parentheses
            return self.call_func(lname, [], n)
        return None

    def ev(self, n: Node):
        k = n.k
        if k == "lit":
            return LitV(n.v)
        if k == "name":
            v = self.resolve_name(n)
            return OPAQUE if v is None else v
        if k == "list":
            return ListV([self.ev(x) for x in n.a])
        if k == "alt":
            return Alt([self.ev(x) for x in n.a])
        if k == "bin":
            # 'X as T' keeps X; 'A + B' on signature sets joins them
            vals = [self.ev(x) for x in n.a]
            items = []
            for v in vals:
                if isinstance(v, ListV):
                    items.extend(v.items)
            if items:
                return ListV(items)
            return next((v for v in vals if isinstance(v, (Def, Alt))), OPAQUE)
        if k == "unary":
            return self.ev(n.a[0])
        if k == "index":
            return self.ev(n.a[0])
        if k == "anon":
            return OPAQUE
        if k == "member":
            base = self.ev(n.a[0])
            return base if isinstance(base, (Def, Alt)) else OPAQUE
        if k == "call":
            return self.ev_call(n)
        return OPAQUE

    def ev_call(self, n: Node):
        callee = n.v
        if callee.k == "member":
            base = self.ev(callee.a[0])
            meth = callee.v.lower()
            if meth == "setgetcp" and n.a:
                cb = n.a[0].v if n.a[0].k == "name" else "?"
                return self.with_getcp(base, cb)
            if isinstance(base, (Def, Alt)):
                return base
            return OPAQUE
        if callee.k != "name":
            return OPAQUE
        name = callee.v
        lname = name.lower()
        if lname in ("registerrecorddef", "registerrefrecorddef"):
            self.register(n)
            return OPAQUE
        found, v = self.env_lookup(lname)
        if found:
            return v if isinstance(v, (Def, Alt)) else OPAQUE
        if lname in self.funcs:
            return self.call_func(lname, n.a, n)
        bv = self.binds.get(lname)
        if bv:
            v = self.bind_value(lname, callee)
            return v if isinstance(v, (Def, Alt)) else OPAQUE
        if lname.startswith("wb"):
            rt = self.ctor_types.get(lname)
            if rt is None or is_def_type(rt):
                if rt is None:
                    self.unresolved[name] = self.unresolved.get(name, 0) + 1
                return self.make_def(name, n)
            return OPAQUE
        # game selectors (TwbGameDef.IsFO4Plus(a, b) ...): Starfield takes the first argument when the test holds
        if lname in GAME_SELECTORS and len(n.a) >= 2:
            return self.ev(n.a[0] if GAME_SELECTORS[lname] else n.a[1])
        # IfThen / other helpers: keep the defs among the arguments
        vals = [self.ev(a) for a in n.a]
        defs = [v for v in vals if isinstance(v, (Def, Alt))]
        if defs:
            return defs[0] if len(defs) == 1 else Alt(defs)
        return OPAQUE

    def with_getcp(self, base, cb):
        if isinstance(base, Def):
            return Def(base.ctor, base.sig, base.name, base.cp, base.children, base.getcp + [cb], base.line, base.file)
        if isinstance(base, Alt):
            return Alt([self.with_getcp(v, cb) for v in base.vals])
        return base

    def make_def(self, ctor: str, n: Node) -> Def:
        args = [self.ev(a) for a in n.a]
        sig = []
        if args:
            a0 = args[0]
            if isinstance(a0, Sig):
                sig = [a0.s]
            elif isinstance(a0, ListV) and a0.items and all(isinstance(x, Sig) for x in a0.items) \
                    and not ctor.lower().startswith("wbformid"):
                sig = [x.s for x in a0.items]
        name = next((a.v for a in args if isinstance(a, LitV) and isinstance(a.v, str)), "")
        cp = next((a.c for a in args if isinstance(a, CP)), None)
        children = []
        for a in args:
            collect_defs(a, children)
        lc = ctor.lower()
        if lc == "wbunused":
            name, cp = "Unused", "cpIgnore"   # wbInterface.pas wbUnused: always 'Unused', cpIgnore
        if cp is None:
            cp = "cpNormal"
        if name == "Unused" and cp == "cpNormal":
            cp = "cpIgnore"   # TwbNamedDef.Create: a member named 'Unused' at cpNormal becomes cpIgnore
        return Def(ctor, sig, name, cp, children, [], n.line, n.file)

    def pick_overload(self, fns, args, ref):
        if len(fns) == 1:
            return fns[0]
        first_is_sig = bool(args) and args[0].k == "name" and args[0].v in self.sigs
        ok = []
        for fn in fns:
            req = sum(1 for p in fn.params if p[1] is None)
            if req <= len(args) <= len(fn.params):
                ok.append(fn)
        if not ok:
            ok = fns
        for fn in ok:
            fsig = bool(fn.params) and "twbsignature" in fn.params[0][2].lower().replace(" ", "")
            if fsig == first_is_sig:
                return fn
        return ok[0]

    def call_func(self, lname, args: list, ref: Node):
        fns = self.funcs[lname]
        fn = self.pick_overload(fns, args, ref)
        if self.stack.count(lname) >= 3 or len(self.stack) > 60:
            return OPAQUE
        env = {}
        for idx, (pname, default, _t) in enumerate(fn.params):
            if idx < len(args):
                env[pname.lower()] = self.ev(args[idx])
            elif default is not None:
                env[pname.lower()] = self.ev(default)
            else:
                env[pname.lower()] = OPAQUE
        self.stack.append(lname)
        self.env_stack.append(env)
        try:
            results = []
            toks = fn.toks
            p = Parser(toks)
            b, e = fn.body
            j = b
            while j < e:
                t = toks[j]
                if t.kind == "id":
                    lv = t.val.lower()
                    if lv == "result" and toks[j + 1].val == ":=":
                        p.i = j + 2
                        results.append(self.ev(p.expr()))
                        j = p.i
                        continue
                    if lv == "exit" and toks[j + 1].val == "(":
                        p.i = j + 2
                        results.append(self.ev(p.expr()))
                        j = p.i
                        continue
                    if lv in ("registerrecorddef", "registerrefrecorddef") and toks[j + 1].val == "(":
                        p.i = j
                        self.ev(p.expr())
                        j = p.i
                        continue
                    if lv in ("function", "procedure") and toks[j - 1].val in (":=", "(", ","):
                        p.i = j
                        p.anon()
                        j = p.i
                        continue
                j += 1
            defs = [r for r in results if isinstance(r, (Def, Alt))]
            if not defs:
                return OPAQUE
            return defs[0] if len(defs) == 1 else Alt(defs)
        finally:
            self.stack.pop()
            self.env_stack.pop()

    def register(self, n: Node):
        args = [self.ev(a) for a in n.a]
        sig = next((a.s for a in args if isinstance(a, Sig)), None)
        name = next((a.v for a in args if isinstance(a, LitV) and isinstance(a.v, str)), "")
        members = None
        for a in args:
            if isinstance(a, ListV) and any(isinstance(x, (Def, Alt)) for x in a.items):
                members = a
        if sig is None or members is None:
            log(f"skip registration at {n.file}:{n.line}: no signature or member list")
            return
        self.records.append({"sig": sig, "name": name, "members": members.items, "line": n.line,
                             "file": n.file, "ref": n.v.v.lower() == "registerrefrecorddef"})


def is_def_type(rt: str) -> bool:
    r = rt.lower()
    if "formater" in r or "formatter" in r or r in ("iwbenumdef", "iwbflagsdef"):
        return False
    return r.startswith("iwb") and r.endswith("def")


def collect_defs(v, out):
    if isinstance(v, Def):
        out.append(v)
    elif isinstance(v, Alt):
        for x in v.vals:
            collect_defs(x, out)
    elif isinstance(v, ListV):
        for x in v.items:
            collect_defs(x, out)


def leaves(d: Def, path=""):
    p = f"{path}/{d.name}" if path else (d.name or d.ctor)
    if not d.children:
        yield p, d
        return
    for c in d.children:
        yield from leaves(c, p)


def all_sigs(d: Def, out: list):
    for s in d.sig:
        if s not in out:
            out.append(s)
    for c in d.children:
        all_sigs(c, out)


# ----------------------------------------------------------------------------------------------- xEdit side
def ctor_return_types(interface_text: str) -> dict[str, str]:
    """name (lower) -> return type of every wb* function declared in wbInterface.pas's interface part."""
    head = interface_text.split("\nimplementation", 1)[0]
    out = {}
    for m in re.finditer(r"function\s+(wb\w+)\s*(\((?:[^()]|\([^()]*\))*\))?\s*:\s*(\w+)", head, re.I):
        nm, rt = m.group(1).lower(), m.group(3)
        if nm not in out or is_def_type(rt):
            out[nm] = rt
    return out


def read_signatures(text: str) -> set[str]:
    return set(re.findall(r"^\s*(\w{4})\s*:\s*TwbSignature\s*=", text, re.M))


def member_entry(v) -> dict:
    defs = []
    collect_defs(v, defs)
    sigs, names, own = [], [], []
    leafs = []
    getcps = []
    for d in defs:
        all_sigs(d, sigs)
        if d.name and d.name not in names:
            names.append(d.name)
        if d.cp not in own:
            own.append(d.cp)
        for p, lf in leaves(d):
            leafs.append((p, lf.cp, lf.getcp))
        stack = [d]
        while stack:
            x = stack.pop()
            for g in x.getcp:
                if g not in getcps:
                    getcps.append(g)
            stack.extend(x.children)
    counts: dict[str, int] = {}
    for _p, cp, _g in leafs:
        counts[cp] = counts.get(cp, 0) + 1
    buckets = {BUCKET[c] for c in counts if c in BUCKET}
    live = buckets - {"ignore"}
    if live:
        prio = max(live, key=lambda b: SEVERITY[b])
    elif buckets:
        prio = "ignore"
    else:
        prio = BUCKET.get(own[0], "normal") if own else "normal"
    top_cp = max(counts, key=lambda c: (SEVERITY[BUCKET[c]], CP_ORDER.index(c))) if counts else (own[0] if own else "cpNormal")
    if prio != "ignore":
        cands = [c for c in counts if BUCKET[c] == prio]
        top_cp = max(cands, key=CP_ORDER.index) if cands else top_cp
    label = " | ".join(names) if names else (sigs[0] if sigs else "?")
    e = {"xedit": label, "sig": sigs, "cp": top_cp, "priority": prio,
         "member_cp": own[0] if len(own) == 1 else own, "leaf_cps": dict(sorted(counts.items(), key=lambda kv: CP_ORDER.index(kv[0])))}
    if "cpBenignIfAdded" in counts:
        e["benign_if_added"] = True
    if getcps:
        e["dynamic"] = getcps
    if len(buckets) > 1:
        e["leaves"] = [{"path": p, "cp": cp} for p, cp, _g in leafs if BUCKET.get(cp) != prio][:40]
    return e


# ----------------------------------------------------------------------------------------------- Mutagen side
SIG_ATTRS = {"recordType", "counterRecType", "endMarker", "endMarkerType", "femaleMarker", "maleMarker",
             "itemEndMarkerType", "itemMarkerType", "markerType", "genderEnumRecord", "boolAsMarker"}
NS = "{http://tempuri.org/LoquiSource.xsd}"


def load_mutagen(records_dir: str):
    objs = {}
    for root, _d, files in os.walk(records_dir):
        for fn in files:
            if not fn.endswith(".xml"):
                continue
            try:
                tree = ET.parse(os.path.join(root, fn))
            except ET.ParseError as ex:
                log(f"Mutagen XML parse error in {fn}: {ex}")
                continue
            for o in tree.getroot().iter(NS + "Object"):
                objs[o.get("name")] = o
    return objs


def tag(el):
    return el.tag.replace(NS, "")


def el_sigs(el, objs, depth=0, seen=None) -> set[str]:
    seen = seen or set()
    out = {el.get(a) for a in SIG_ATTRS if el.get(a) and len(el.get(a)) == 4}
    ref = el.get("refName")
    # follow embedded objects (RefDirect, RefList, Ref/Direct) only; a FormLink's refName names the linked
    # record type, not subrecords of this record
    if ref and depth < 6 and tag(el) in ("RefDirect", "RefList", "Direct"):
        target = objs.get(ref.split(".")[-1])
        if target is None or target.get("objType") != "Record":
            out |= obj_sigs(ref, objs, depth + 1, seen)
    for c in el:
        if tag(c) in ("Fields",):
            continue
        out |= el_sigs(c, objs, depth + 1, seen)
    return out


def obj_sigs(name, objs, depth=0, seen=None) -> set[str]:
    seen = seen if seen is not None else set()
    name = name.split(".")[-1]
    if name in seen or depth > 6:
        return set()
    seen.add(name)
    o = objs.get(name)
    if o is None:
        return set()
    if o.get("recordType"):
        out = {o.get("recordType")}
    else:
        out = set()
        f = o.find(NS + "Fields")
        if f is not None:
            for c in f:
                out |= el_sigs(c, objs, depth + 1, seen)
    for sub in objs.values():   # subclasses of an abstract object (AComponent ...)
        if (sub.get("baseClass") or "").split(".")[-1] == name:
            out |= obj_sigs(sub.get("name"), objs, depth + 1, seen)
    return out


def mutagen_fields(o, objs):
    """Top-level property name -> set of subrecord signatures, in order, including base classes."""
    out: list[tuple[str, set]] = []
    chain = []
    cur = o
    while cur is not None:
        chain.append(cur)
        base = (cur.get("baseClass") or "").split(".")[-1]
        cur = objs.get(base) if base and base not in ("StarfieldMajorRecord",) else None
    for obj in reversed(chain):
        f = obj.find(NS + "Fields")
        if f is None:
            continue
        for c in f:
            t = tag(c)
            if t == "Data":
                dsig = c.get("recordType") or "DATA"   # a record-level <Data> without recordType reads DATA
                inner = c.find(NS + "Fields")
                if inner is not None:
                    for d in inner:
                        if d.get("name") and tag(d) not in ("Break", "CustomLogic", "Marker"):
                            out.append((d.get("name"), {dsig} if dsig else el_sigs(d, objs)))
                continue
            if t in ("Break", "CustomLogic", "Marker") or not c.get("name"):
                continue
            out.append((c.get("name"), el_sigs(c, objs)))
    return out


def dll_names(dll: str) -> set[str] | None:
    try:
        with open(dll, "rb") as fh:
            data = fh.read()
    except OSError:
        return None
    # .NET keeps member names as UTF-8 in the #Strings heap, often merged into longer entries ("get_X",
    # "<X>k__BackingField", "IFooGetter.get_X"), so collect every identifier and the X of every get_X.
    out = set()
    for m in re.findall(rb"[A-Za-z_][A-Za-z0-9_]*", data):
        s = m.decode("ascii")
        out.add(s)
        if s.startswith("get_"):
            out.add(s[4:])
    return out


def short_sigs(sigs: list) -> list:
    """Output only: a big block's signature list is shortened to its ends (matching used the full list)."""
    if len(sigs) > BIG_BLOCK:
        return [sigs[0], f"... {len(sigs) - 2} more ...", sigs[-1]]
    return sigs


# ----------------------------------------------------------------------------------------------- build
BIG_BLOCK = 20

HEADER_FIELDS = {
    # record header (wbDefinitionsCommon.pas wbRecordHeader), under the Mutagen names of the header fields
    "FormVersion": ("cpIgnore", "Form Version"),
    "VersionControl": ("cpIgnore", "Version Control Info 1"),
    "Version2": ("cpIgnore", "Version Control Info 2"),
    "FormKey": ("cpFormID", "FormID"),
    # Record flags: cpNormal, but xEdit ignores flag differences on a copy that is deleted or a partial form
    # (TwbFlagsDef.FlagGetCP), and per-flag ignore masks can switch single flags off (not tabulated here).
    "MajorFlags": ("cpNormal", "Record Flags"),
}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        h.update(fh.read())
    return h.hexdigest()


def git_head(path):
    try:
        r = subprocess.run(["git", "-C", path, "rev-parse", "HEAD"], capture_output=True, text=True, timeout=30)
        return r.stdout.strip() or None
    except (OSError, subprocess.SubprocessError):
        return None


def extract_mutagen(repo: str, commit: str, dest: str) -> str | None:
    """Unpack the Starfield record XML of one Mutagen commit into dest with 'git archive'; return its folder."""
    try:
        r = subprocess.run(["git", "-C", repo, "archive", "--format=tar", commit, MUTAGEN_SUBDIR],
                           capture_output=True, timeout=600)
    except (OSError, subprocess.SubprocessError) as ex:
        log(f"git archive failed: {ex}")
        return None
    if r.returncode != 0:
        log(f"git archive {commit} in {repo} failed: {r.stderr.decode(errors='replace').strip()}")
        return None
    root = os.path.realpath(dest)
    n = 0
    with tarfile.open(fileobj=io.BytesIO(r.stdout)) as tf:
        for m in tf.getmembers():
            if not m.isfile() or not m.name.endswith(".xml"):
                continue
            target = os.path.realpath(os.path.join(root, m.name))
            if not target.startswith(root + os.sep):
                continue
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with tf.extractfile(m) as src, open(target, "wb") as out:
                out.write(src.read())
            n += 1
    log(f"Mutagen XML: {n} files from {repo} at {commit}")
    return os.path.join(root, *MUTAGEN_SUBDIR.split("/")) if n else None


def build(xedit_core: str, mutagen_records: str, mutagen_dll: str | None, source_commit: str,
          mutagen_source: str = "", release: str = DEF_XEDIT_RELEASE) -> dict:
    files = {}
    for fn in ("wbDefinitionsCommon.pas", "wbDefinitionsSF1.pas"):
        with open(os.path.join(xedit_core, fn), encoding="utf-8", errors="replace") as fh:
            files[fn] = fh.read()
    with open(os.path.join(xedit_core, "wbInterface.pas"), encoding="utf-8", errors="replace") as fh:
        iface = fh.read()
    with open(os.path.join(xedit_core, "wbDefinitionsSignatures.pas"), encoding="utf-8", errors="replace") as fh:
        sigs = read_signatures(fh.read())
    ctor = ctor_return_types(iface)
    log(f"signatures: {len(sigs)}, wbInterface constructors: {len(ctor)}")
    src = Source(files, sigs, ctor)
    log(f"helpers: {len(src.funcs)}, bindings: {len(src.binds)}")
    src.find_records("wbDefinitionsSF1.pas")
    log(f"records registered: {len(src.records)}")
    if src.unresolved:
        log("wb* calls not declared in wbInterface.pas (treated as constructors): "
            + ", ".join(f"{k} x{v}" for k, v in sorted(src.unresolved.items())))

    objs = load_mutagen(mutagen_records) if mutagen_records and os.path.isdir(mutagen_records) else {}
    by_sig: dict[str, list] = {}
    for o in objs.values():
        if o.get("objType") == "Record" and o.get("abstract") != "true":
            rt = o.get("recordType")
            if not rt:
                # concrete subclasses of an abstract record (GameSettingInt under GameSetting ...)
                base = objs.get((o.get("baseClass") or "").split(".")[-1])
                rt = base.get("recordType") if base is not None else None
            if rt:
                by_sig.setdefault(rt, []).append(o)
    names = dll_names(mutagen_dll) if mutagen_dll else None
    log(f"Mutagen objects: {len(objs)}, record classes: {len(by_sig)}, DLL names: {len(names) if names else 'none'}")

    records = {}
    types = {}
    stats = {"records": 0, "fields": 0, "fields_mapped": 0, "mutagen_fields": 0, "mutagen_mapped": 0,
             "mutagen_checked_in_dll": 0, "mutagen_missing_in_dll": 0}
    for rec in src.records:
        sig = rec["sig"]
        if sig in records:
            log(f"{sig}: registered twice (line {records[sig]['line']} and {rec['line']}); keeping the first")
            continue
        fields = []
        for m in rec["members"]:
            e = member_entry(m)
            if not e["sig"] and e["xedit"] == "?":
                continue
            fields.append(e)
        classes = sorted(by_sig.get(sig, []), key=lambda o: o.get("name"))
        mtypes = [o.get("name") for o in classes]
        mfields = [("EditorID", {"EDID"})]
        seen_m = {"EditorID"}
        for o in classes:
            for mn, ss in mutagen_fields(o, objs):
                if mn not in seen_m:
                    seen_m.add(mn)
                    mfields.append((mn, ss))
        # Each Mutagen field goes to the xEdit member that shares the most of its subrecords; on a tie, the
        # member with the fewest subrecords (the most specific: FULL goes to "Name", not to the component block).
        choice = {}
        for mn, ss in mfields:
            best = None
            for idx, e in enumerate(fields):
                ov = len(ss & set(e["sig"]))
                # a big block of subrecords (the base form components between BFCB and BFCE) only takes a field
                # that starts with the block's own opening signature, not every field that reuses an inner one
                if ov and len(e["sig"]) > BIG_BLOCK and e["sig"][0] not in ss:
                    ov = 0
                if ov:
                    key = (ov, -len(e["sig"]), -idx)
                    if best is None or key > best[0]:
                        best = (key, idx)
            if best:
                choice[mn] = best[1]
        for idx, e in enumerate(fields):
            e["mutagen"] = [mn for mn, _ss in mfields if choice.get(mn) == idx]
            if not e["mutagen"]:
                e["unmapped"] = ("no Mutagen field reads these subrecords" if mtypes else "no Mutagen record class")
        by_m = {}
        for mn, ss in mfields:
            if mn in choice:
                best = fields[choice[mn]]
                ent = {"priority": best["priority"], "cp": best["cp"], "sig": short_sigs(best["sig"]),
                       "xedit": best["xedit"]}
                if best.get("benign_if_added"):
                    ent["benign_if_added"] = True
                if best.get("dynamic"):
                    ent["dynamic"] = best["dynamic"]
            else:
                ent = {"priority": "normal", "cp": None, "sig": short_sigs(sorted(ss)), "xedit": None,
                       "unmapped": "no xEdit member with these subrecords; the default (normal) applies"}
            if names is not None and mn not in ("EditorID",):
                if mn in names:
                    stats["mutagen_checked_in_dll"] += 1
                else:
                    stats["mutagen_missing_in_dll"] += 1
                    ent["dll_check"] = "name not found in the Mutagen 0.54.4 DLL"
            by_m.setdefault(mn, ent)
        stats["records"] += 1
        stats["fields"] += len(fields)
        stats["fields_mapped"] += sum(1 for e in fields if e["mutagen"])
        stats["mutagen_fields"] += len(by_m)
        stats["mutagen_mapped"] += sum(1 for v in by_m.values() if v.get("xedit"))
        for e in fields:
            if len(e["sig"]) > BIG_BLOCK:
                e["sig_count"] = len(e["sig"])
                e["sig"] = short_sigs(e["sig"])
        records[sig] = {"xedit_name": rec["name"], "mutagen_types": mtypes, "line": rec["line"],
                        "fields": fields, "by_mutagen": by_m}
        for mt in mtypes:
            types[mt] = sig
    if not records:
        raise SystemExit("no records found in wbDefinitionsSF1.pas: check --xedit-core")

    in_files = {fn: sha256(os.path.join(xedit_core, fn)) for fn in
                ("wbDefinitionsSF1.pas", "wbDefinitionsCommon.pas", "wbInterface.pas", "wbDefinitionsSignatures.pas")}
    return {
        "source": {
            "xedit": "xEdit (TES5Edit), https://github.com/TES5Edit/TES5Edit",
            "commit": source_commit,
            "release": release,
            "checkout_head": git_head(os.path.dirname(xedit_core)),
            "files": in_files,
            "mutagen": "Mutagen.Bethesda.Starfield 0.54.4 (names from the Loqui record XML, checked against the 0.54.4 DLL)",
            "mutagen_xml": mutagen_source,
            "mutagen_dll_checked": bool(mutagen_dll),
            "generator": "data/gen/gen_field_priorities.py (sf-mod-check)",
        },
        "license": "MPL-2.0",
        "credit": CREDIT,
        "buckets": BUCKET,
        "severity": SEVERITY,
        "default": {"priority": "normal", "cp": "cpNormal",
                    "why": "every wb* constructor in wbInterface.pas defaults aPriority to cpNormal"},
        "rules": [
            "A member named 'Unused' that is left at cpNormal becomes cpIgnore (TwbNamedDef.Create); wbUnused is always cpIgnore.",
            "xEdit rates a difference by the priority of the value that differs (the leaf), not the container: 'priority' is the highest leaf priority in the member, ignoring cpIgnore leaves.",
            "cpFormID becomes cpCritical, except in GMST and DFOB where it is cpBenign.",
            "An injected record (its master copy is in a plugin that doesn't own the FormID) raises every cpNormal or higher difference to critical, except in GMST and DFOB.",
            "cpBenignIfAdded is benign only when the first copy lacks the value and no later plugin overrides it; otherwise it counts as normal.",
            "'dynamic' names a callback that can change the priority per element at run time (for example wbModelInfoGetCP).",
        ],
        "record_header": {k: {"cp": v[0], "priority": BUCKET[v[0]], "xedit": v[1]} for k, v in HEADER_FIELDS.items()},
        "stats": stats,
        "mutagen_types": dict(sorted(types.items())),
        "records": dict(sorted(records.items())),
    }


MPL_HEADER = [
    "This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of the MPL",
    "was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.",
    "Original: https://github.com/TES5Edit/TES5Edit (ElminsterAU and the xEdit team). See NOTICE.",
]


def flags_of(ent: dict) -> list:
    out = []
    if ent.get("benign_if_added"):
        out.append("benign-if-added")
    for cb in ent.get("dynamic") or []:
        out.append(f"dynamic={cb}")
    return out


def table_lines(data: dict) -> list:
    """The trimmed table sf-mod-check reads: one line per Mutagen field that maps to an xEdit member."""
    src, s = data["source"], data["stats"]
    mut_commit = (src.get("mutagen_xml") or "").rsplit(" at ", 1)[-1][:8] or "?"
    lines = [
        "# Field conflict priorities for sf-mod-check conflict: <record type> <Mutagen field> <priority> [flags].",
        f"# {CREDIT}.",
        f"# Describes xEdit commit {src['commit']} (release {src['release']}): wbDefinitionsSF1.pas with "
        "wbDefinitionsCommon.pas. Field names from Mutagen's Starfield record XML at commit "
        f"{mut_commit} (Mutagen.Bethesda.Starfield 0.54.4).",
        "# Made by data/gen/gen_field_priorities.py. Priorities: ignore, benign, normal, critical; "
        "a field that isn't listed is normal (xEdit's default, cpNormal).",
        "# Flags: benign-if-added = benign only when the earlier copy lacks the value and no later plugin overrides "
        "it, else normal; dynamic=<callback> = xEdit changes the priority per element at run time (the static value "
        "is given).",
        "# 'type <Mutagen class> <record type>' maps Mutagen's record classes to record types; "
        "'header <field> <priority>' covers the record header.",
        f"# {s['records']} record types, {s['fields']} xEdit members ({s['fields_mapped']} mapped), "
        f"{s['mutagen_fields']} Mutagen fields ({s['mutagen_mapped']} listed below).",
        "#",
    ] + [f"# {x}" for x in MPL_HEADER]
    for mt, sig in data["mutagen_types"].items():
        lines.append(f"type {mt} {sig}")
    for k, v in data["record_header"].items():
        lines.append(f"header {k} {v['priority']}")
    for sig, rec in data["records"].items():
        for mn, ent in sorted(rec["by_mutagen"].items()):
            if not ent.get("xedit"):
                continue   # no xEdit member: the default (normal) applies
            lines.append(" ".join([sig, mn, ent["priority"]] + flags_of(ent)))
    return lines


def main(argv=None):
    global VERBOSE
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--xedit-core", help="the Core folder of an xEdit source checkout (holds wbDefinitionsSF1.pas)")
    ap.add_argument("--mutagen-records", help="Mutagen.Bethesda.Starfield/Records folder (Loqui XML) of a Mutagen "
                    "source tree; or use --mutagen-repo")
    ap.add_argument("--mutagen-repo", help="a Mutagen git clone; the record XML is read at --mutagen-commit with git")
    ap.add_argument("--mutagen-commit", default=DEF_MUTAGEN_COMMIT, help="Mutagen commit (default: the 0.54.4 package's)")
    ap.add_argument("--mutagen-dll", default=DEF_DLL, help="Mutagen.Bethesda.Starfield.dll 0.54.4 to check names "
                    "against (default: the NuGet cache; skipped with a log line when missing)")
    ap.add_argument("--source-commit", default=DEF_XEDIT_COMMIT, help="xEdit commit the table describes")
    ap.add_argument("--source-release", default=DEF_XEDIT_RELEASE, help="xEdit release the table describes")
    ap.add_argument("--out", default=DEF_OUT, help="the table to write (default: data/field-priorities.txt)")
    ap.add_argument("--json", help="also write the full view (xEdit members, leaves, input checksums) as JSON")
    ap.add_argument("--log", help="write a log of what was read and decided")
    ap.add_argument("--verbose", action="store_true", help="print the log to stderr as well")
    a = ap.parse_args(argv)
    VERBOSE = a.verbose
    LOG.clear()
    if not a.xedit_core:
        print("error: no xEdit source given. Pass --xedit-core <xEdit source>/Core "
              "(clone https://github.com/TES5Edit/TES5Edit).", file=sys.stderr)
        return 2
    if not a.mutagen_records and not a.mutagen_repo:
        print("error: no Mutagen source given. Pass --mutagen-repo <Mutagen clone> "
              "(https://github.com/Mutagen-Modding/Mutagen) or --mutagen-records <Records folder>.", file=sys.stderr)
        return 2
    for fn in ("wbDefinitionsSF1.pas", "wbDefinitionsCommon.pas", "wbInterface.pas", "wbDefinitionsSignatures.pas"):
        p = os.path.join(a.xedit_core, fn)
        if not os.path.isfile(p):
            print(f"error: {p} not found. Pass --xedit-core <xEdit source>/Core.", file=sys.stderr)
            return 1
    dll = a.mutagen_dll if a.mutagen_dll and os.path.isfile(a.mutagen_dll) else None
    if a.mutagen_dll and not dll:
        log(f"Mutagen DLL {a.mutagen_dll} not found: names are not checked")
    with tempfile.TemporaryDirectory(prefix="fieldprio-") as tmp:
        records_dir, mut_src = a.mutagen_records, a.mutagen_records
        if not records_dir:
            records_dir = extract_mutagen(a.mutagen_repo, a.mutagen_commit, tmp)
            mut_src = f"{os.path.basename(os.path.normpath(a.mutagen_repo))} at {a.mutagen_commit}"
        else:
            mut_src = f"{os.path.basename(os.path.normpath(records_dir))} folder"
        if not records_dir or not os.path.isdir(records_dir):
            print(f"error: Mutagen record XML not found ({mut_src}). Pass --mutagen-records "
                  "<Mutagen>/Mutagen.Bethesda.Starfield/Records, or --mutagen-repo with a clone that has "
                  f"commit {a.mutagen_commit}.", file=sys.stderr)
            return 1
        try:
            data = build(a.xedit_core, records_dir, dll, a.source_commit, mut_src, a.source_release)
        except SystemExit as ex:
            print(f"error: {ex}", file=sys.stderr)
            return 1
    with open(a.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(table_lines(data)) + "\n")
    if a.json:
        with open(a.json, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(data, fh, indent=1, ensure_ascii=False)
            fh.write("\n")
    s = data["stats"]
    msg = (f"wrote {a.out}: {s['records']} record types, {s['fields']} xEdit members "
           f"({s['fields_mapped']} mapped), {s['mutagen_fields']} Mutagen fields ({s['mutagen_mapped']} with an xEdit priority)")
    log(msg)
    print(msg)
    if a.log:
        with open(a.log, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("\n".join(LOG) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
