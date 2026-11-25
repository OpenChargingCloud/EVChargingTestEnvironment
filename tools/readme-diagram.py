#!/usr/bin/env python3
"""
Draws the overview of the EV charging test environment once for every CLI
repository below libs/, with the circle of that repository filled pastel red,
writes it to libs/<repo>/docs/EVChargingTestEnvironment.svg and puts it as the
first image below the heading of libs/<repo>/README.md.

    EV -> CS -> LC -> GW -> CSMS -> RN -> EMSP
    CS, LC -> EM        LC -> GO        EV, CS, EMSP -> SP
    DNS, NTS, Log, Download, PKI on their own

Run it again after a change: it rewrites the SVGs and leaves a README that
already shows the image as it is.
"""

import pathlib
import sys

ROOT      = pathlib.Path(__file__).resolve().parent.parent
SVG_PATH  = "docs/EVChargingTestEnvironment.svg"

R         = 32
COL       = [70 + 130 * i for i in range(7)]   # x of the chain
Y_TOP     = 60
Y_CHAIN   = 180
Y_GRID    = 290
Y_PHONE   = 390
WIDTH     = 920
HEIGHT    = 440

# CS and LC have four neighbours each, one on every side: the chain left and
# right, the meter above both, and below the smartphone (CS) and the grid
# operator (LC). The smartphone sits below CS, so EV and EMSP reach it from
# the sides along the bottom lane, below the grid operator.

SERVICES  = ["DNS", "NTS", "Log", "Download", "PKI"]

# id -> (label, x, y)
NODES = {
    "EV":       ("EV",       COL[0],                   Y_CHAIN),
    "CS":       ("CS",       COL[1],                   Y_CHAIN),
    "LC":       ("LC",       COL[2],                   Y_CHAIN),
    "GW":       ("GW",       COL[3],                   Y_CHAIN),
    "CSMS":     ("CSMS",     COL[4],                   Y_CHAIN),
    "RN":       ("RN",       COL[5],                   Y_CHAIN),
    "EMSP":     ("EMSP",     COL[6],                   Y_CHAIN),
    "EM":       ("EM",       (COL[1] + COL[2]) // 2,   Y_TOP),
    "GO":       ("GO",       COL[2],                   Y_GRID),
    "SP":       ("SP",       COL[1],                   Y_PHONE),
    **{s: (s, 500 + 90 * i, Y_TOP) for i, s in enumerate(SERVICES)},
}

# repository below libs/ -> (node, what the image's alt text calls it)
REPOS = {
    "EV":                   ("EV",       "the EV"),
    "ChargingStation":      ("CS",       "the charging station"),
    "LocalController":      ("LC",       "the local controller"),
    "Gateway":              ("GW",       "the gateway"),
    "CSMS":                 ("CSMS",     "the CSMS"),
    "RoamingHub":           ("RN",       "the roaming hub"),
    "EMSP":                 ("EMSP",     "the EMSP"),
    "ModbusTLSEnergyMeter": ("EM",       "the energy meter"),
    "GridOperator":         ("GO",       "the grid operator"),
    "Smartphone":           ("SP",       "the smartphone"),
    "DNSServer":            ("DNS",      "the DNS server"),
    "NTSServer":            ("NTS",      "the NTS server"),
    "LogServer":            ("Log",      "the log server"),
    "DownloadServer":       ("Download", "the download server"),
    "PKI":                  ("PKI",      "the PKI"),
}

LINE       = "#6e7781"
TEXT       = "#24292f"
FILL       = "#ffffff"
HIGH_FILL  = "#f8c4c4"
HIGH_LINE  = "#d9534f"
FONT       = "'Segoe UI', Helvetica, Arial, sans-serif"


def font_size(label: str) -> int:
    return {1: 16, 2: 16, 3: 15, 4: 14}.get(len(label), 11)


def path(points) -> str:
    d = "M " + " L ".join(f"{x} {y}" for x, y in points)
    return f'  <path d="{d}" fill="none" stroke="{LINE}" stroke-width="2" marker-end="url(#arrow)"/>'


def edges():
    x = lambda n: NODES[n][1]
    y = lambda n: NODES[n][2]

    chain = ["EV", "CS", "LC", "GW", "CSMS", "RN", "EMSP"]
    for a, b in zip(chain, chain[1:]):
        yield path([(x(a) + R, Y_CHAIN), (x(b) - R, Y_CHAIN)])

    # CS and LC -> EM: up, then sideways into the meter
    yield path([(x("CS"), Y_CHAIN - R), (x("CS"), y("EM")), (x("EM") - R, y("EM"))])
    yield path([(x("LC"), Y_CHAIN - R), (x("LC"), y("EM")), (x("EM") + R, y("EM"))])

    # LC -> GO: down
    yield path([(x("LC"), Y_CHAIN + R), (x("GO"), y("GO") - R)])

    # CS -> SP: down; EV and EMSP -> SP: down, then sideways into the smartphone
    yield path([(x("CS"),   Y_CHAIN + R), (x("SP"), y("SP") - R)])
    yield path([(x("EV"),   Y_CHAIN + R), (x("EV"),   y("SP")), (x("SP") - R, y("SP"))])
    yield path([(x("EMSP"), Y_CHAIN + R), (x("EMSP"), y("SP")), (x("SP") + R, y("SP"))])


def svg(highlight: str) -> str:
    out = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{WIDTH}" height="{HEIGHT}" viewBox="0 0 {WIDTH} {HEIGHT}">',
        f'  <title>EV charging test environment: {", ".join(label for label, _, _ in NODES.values())}</title>',
        '  <defs>',
        f'    <marker id="arrow" viewBox="0 0 10 10" refX="10" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">',
        f'      <path d="M 0 0 L 10 5 L 0 10 z" fill="{LINE}"/>',
        '    </marker>',
        '  </defs>',
        f'  <rect x="1" y="1" width="{WIDTH - 2}" height="{HEIGHT - 2}" rx="12" fill="#ffffff" stroke="#d0d7de" stroke-width="2"/>',
        # DNS, NTS, Log, Download and PKI stand apart from the chain
        f'  <rect x="{NODES[SERVICES[0]][1] - R - 14}" y="{Y_TOP - R - 14}" width="{NODES[SERVICES[-1]][1] - NODES[SERVICES[0]][1] + 2 * R + 28}" height="{2 * R + 28}" rx="12" fill="none" stroke="#d0d7de" stroke-width="2" stroke-dasharray="6 5"/>',
    ]
    out.extend(edges())
    for node, (label, x, y) in NODES.items():
        hi = node == highlight
        out.append(f'  <circle cx="{x}" cy="{y}" r="{R}" fill="{HIGH_FILL if hi else FILL}" stroke="{HIGH_LINE if hi else LINE}" stroke-width="{3 if hi else 2}"/>')
        out.append(f'  <text x="{x}" y="{y}" text-anchor="middle" dominant-baseline="central" font-family="{FONT}" font-size="{font_size(label)}" font-weight="bold" fill="{TEXT}">{label}</text>')
    out.append('</svg>')
    return "\n".join(out) + "\n"


def put_into_readme(readme: pathlib.Path, alt: str) -> bool:
    raw  = readme.read_bytes()
    if SVG_PATH.encode() in raw:
        return False
    eol   = b"\r\n" if b"\r\n" in raw else b"\n"
    lines = raw.split(eol)
    at    = next(i for i, l in enumerate(lines) if l.startswith(b"# "))
    image = f"![{alt}]({SVG_PATH})".encode()
    lines[at + 1:at + 1] = [b"", image]
    if lines[at + 3].strip():
        lines.insert(at + 3, b"")
    readme.write_bytes(eol.join(lines))
    return True


def main() -> None:
    for repo, (node, name) in REPOS.items():
        base = ROOT / "libs" / repo
        if not base.is_dir():
            print(f"{repo}: not there", file=sys.stderr)
            continue
        target = base / SVG_PATH
        target.parent.mkdir(exist_ok=True)
        target.write_bytes(svg(node).encode())
        alt = f"The EV charging test environment, {name} ({NODES[node][0]}) highlighted"
        changed = put_into_readme(base / "README.md", alt)
        print(f"{repo:22} {node:8} README {'changed' if changed else 'as it was'}")


if __name__ == "__main__":
    main()
