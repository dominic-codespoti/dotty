#!/usr/bin/env python3
"""Gate NativeAOT publish warnings: IL2xxx/IL3xxx in Dotty's own code fail, third-party ones are listed.

Input is an MSBuild warnings-only file log (`-flp:LogFile=<path>;WarningsOnly`). A warning is
Dotty's own when its origin is a file under `<repo-root>/src/`, or when ILC attributes it to a
member or assembly of Dotty (namespace `Dotty.`, assembly `dotty`/`Dotty.*`). Everything else
(Silk.NET, SkiaSharp, the BCL, ...) is third-party and tolerated, but listed.
"""
from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path

# MSBuild canonical message: "<origin>: [<subcategory> ]warning <code>: <text> [<project>]".
# ILC uses the subcategories "Trim analysis" and "AOT analysis"; Roslyn analyzers use none.
WARNING_PATTERN = re.compile(
    r"^\s*(?P<origin>.*?)\s*:\s*(?:(?P<subcategory>[A-Za-z][A-Za-z ]*?)\s+)?warning\s+"
    r"(?P<code>IL[23]\d{3})\s*:\s*(?P<message>.*?)(?:\s+\[(?P<project>[^\]]*)\])?\s*$"
)
LOCATION_SUFFIX = re.compile(r"\(\d+(?:,\d+)*\)$")
ASSEMBLY_PATTERN = re.compile(r"^Assembly '(?P<assembly>[^']+)'")
MEMBER_PATTERN = re.compile(r"^(?P<member>[A-Za-z_<][^\s(:]*)")


@dataclass(frozen=True)
class IlWarning:
    code: str
    origin: str
    message: str

    def describe(self) -> str:
        return f"{self.code} {self.origin}: {self.message}"


def parse(lines) -> list[IlWarning]:
    """Unique IL2xxx/IL3xxx warnings in first-seen order (MSBuild repeats warnings in its summary)."""
    seen: set[IlWarning] = set()
    warnings: list[IlWarning] = []
    for line in lines:
        match = WARNING_PATTERN.match(line.rstrip("\r\n"))
        if match is None:
            continue
        warning = IlWarning(match["code"], match["origin"].strip(), match["message"].strip())
        if warning not in seen:
            seen.add(warning)
            warnings.append(warning)
    return warnings


def _normalize(path: str) -> str:
    return path.replace("\\", "/").rstrip("/").lower()


def is_own(warning: IlWarning, repo_root: str, own_prefix: str = "Dotty") -> bool:
    origin_path = _normalize(LOCATION_SUFFIX.sub("", warning.origin))
    if origin_path.startswith(_normalize(repo_root) + "/src/"):
        return True
    prefix = own_prefix.lower()
    assembly = ASSEMBLY_PATTERN.match(warning.message)
    if assembly is not None:
        name = assembly["assembly"].lower()
        return name == prefix or name.startswith(prefix + ".")
    member = MEMBER_PATTERN.match(warning.message)
    return member is not None and member["member"].startswith(own_prefix + ".")


def classify(warnings: list[IlWarning], repo_root: str, own_prefix: str = "Dotty") -> tuple[list[IlWarning], list[IlWarning]]:
    own = [warning for warning in warnings if is_own(warning, repo_root, own_prefix)]
    third_party = [warning for warning in warnings if not is_own(warning, repo_root, own_prefix)]
    return own, third_party


def summary_markdown(label: str, own: list[IlWarning], third_party: list[IlWarning]) -> str:
    lines = [f"### NativeAOT warnings ({label})", ""]
    lines.append(f"- Dotty's own code: **{len(own)}** (gating)")
    lines.append(f"- Third-party: {len(third_party)} (tolerated)")
    for title, items in (("Own (fail the job)", own), ("Third-party (tolerated)", third_party)):
        if items:
            lines.extend(["", f"#### {title}", ""])
            lines.extend(f"- `{warning.code}` {warning.origin}: {warning.message}" for warning in items)
    lines.append("")
    return "\n".join(lines)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--log", required=True, type=Path, help="MSBuild warnings-only file log")
    parser.add_argument("--repo-root", required=True, help="repository root as seen by the build")
    parser.add_argument("--label", default="publish", help="label for the report, e.g. the RID")
    parser.add_argument("--own-prefix", default="Dotty", help="namespace/assembly prefix of Dotty's own code")
    parser.add_argument("--summary", type=Path, help="append a Markdown report (e.g. $GITHUB_STEP_SUMMARY)")
    options = parser.parse_args(argv)

    with options.log.open(encoding="utf-8", errors="replace") as handle:
        warnings = parse(handle)
    own, third_party = classify(warnings, options.repo_root, options.own_prefix)

    for warning in third_party:
        print(f"third-party (tolerated): {warning.describe()}")
    for warning in own:
        print(f"::error title=NativeAOT warning in Dotty code::{warning.describe()}")
    print(f"{options.label}: {len(own)} own IL warning(s), {len(third_party)} third-party IL warning(s)")
    if options.summary is not None:
        with options.summary.open("a", encoding="utf-8") as handle:
            handle.write(summary_markdown(options.label, own, third_party))
    return 1 if own else 0


if __name__ == "__main__":
    sys.exit(main())
