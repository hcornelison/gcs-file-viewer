#!/usr/bin/env python3
"""Writes THIRD-PARTY-NOTICES.txt for a published copy of the app.

Usage: scripts/make-notices.py <publish directory>

It reads GcsFileViewer.deps.json in the publish directory to learn exactly which NuGet packages and which
.NET runtime were bundled, then takes each one's licence and copyright from the NuGet package cache. The
packaging scripts run it, so a package's notices always match what is inside that package.
"""
import hashlib
import json
import os
import re
import sys
from pathlib import Path

LICENSES = Path(__file__).resolve().parent.parent / "packaging" / "licenses"
LICENSE_NAMES = {"MIT": "MIT License", "Apache-2.0": "Apache License 2.0", "BSD-3-Clause": "BSD 3-Clause License"}
RULE = "=" * 78


def nuspec_value(nuspec, tag):
    match = re.search(rf"<{tag}[^>]*>(.*?)</{tag}>", nuspec, re.S)
    return re.sub(r"\s+", " ", match.group(1)).strip() if match else ""


def read(path):
    return path.read_text(encoding="utf-8-sig", errors="replace").replace("\r\n", "\n").strip()


def find_file(folder, pattern):
    return next((f for f in sorted(folder.iterdir()) if f.is_file() and re.fullmatch(pattern, f.name, re.I)), None)


def main():
    publish = Path(sys.argv[1])
    deps = json.loads((publish / "GcsFileViewer.deps.json").read_text(encoding="utf-8-sig"))
    cache = Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget" / "packages")
    # The last target is the one for the runtime the app was published for.
    shipped = list(deps["targets"].values())[-1]

    components = []   # (name, version, licence, copyright, url)
    extra = {}        # hash of a bundled notice text -> (names it came with, text)
    used = set()

    def bundle(name, text):
        extra.setdefault(hashlib.sha256(text.encode()).hexdigest(), ([], text))[0].append(name)

    for key, library in sorted(deps["libraries"].items(), key=lambda item: item[0].lower()):
        name, version = key.split("/")
        is_runtime = name.startswith("runtimepack.")
        if library["type"] != "package" and not is_runtime:
            continue
        # Packages that only matter at build time, or on another platform, put no files in the output.
        entry = shipped.get(key, {})
        if not is_runtime and not any(entry.get(kind) for kind in ("runtime", "native", "runtimeTargets")):
            continue

        package = name.removeprefix("runtimepack.")
        folder = cache / package.lower() / version.lower()
        if not folder.is_dir():
            sys.exit(f"{package} {version} is not in the NuGet cache at {cache}")
        nuspec = read(find_file(folder, r".*\.nuspec"))
        licence = nuspec_value(nuspec, "license")
        copyright = nuspec_value(nuspec, "copyright") or f"Copyright (c) {nuspec_value(nuspec, 'authors')}"
        label = ".NET Runtime" if is_runtime else package

        if 'license type="file"' in nuspec:
            bundle(label, read(folder / licence))
            licence = "see its licence below"
        elif licence not in LICENSE_NAMES:
            sys.exit(f"{package} {version} has a licence this script does not know: {licence!r}")
        else:
            used.add(licence)
        components.append((label, version, licence, copyright, nuspec_value(nuspec, "projectUrl")))

        notices = find_file(folder, r"third-?party-?notices(\.txt)?")
        if notices:
            bundle(label, read(notices))

    out = [
        "THIRD-PARTY NOTICES",
        "",
        "GCS File Viewer is released under the MIT License (see LICENSE). This package also contains the",
        "third-party components listed below, each under its own licence. The full text of every licence",
        "follows the list.",
        "",
        RULE,
        "COMPONENTS",
        RULE,
    ]
    for name, version, licence, copyright, url in components:
        out += ["", f"{name} {version}", f"  Licence: {licence}", f"  {copyright}"]
        if url:
            out.append(f"  {url}")
    out += [
        "",
        "Inter font family",
        "  Licence: SIL Open Font License 1.1",
        "  Copyright (c) 2016 The Inter Project Authors",
        "  https://github.com/rsms/inter",
    ]

    for licence in sorted(used):
        out += ["", RULE, LICENSE_NAMES[licence].upper(), RULE, "", read(LICENSES / f"{licence}.txt")]
    out += ["", RULE, "SIL OPEN FONT LICENSE 1.1 (INTER FONT FAMILY)", RULE, "", read(LICENSES / "Inter-OFL-1.1.txt")]

    for names, text in extra.values():
        out += ["", RULE, "NOTICES AND LICENCES DISTRIBUTED WITH: " + ", ".join(sorted(set(names))), RULE, "", text]

    target = publish / "THIRD-PARTY-NOTICES.txt"
    target.write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"Wrote {target} ({len(components) + 1} components)")


if __name__ == "__main__":
    main()
