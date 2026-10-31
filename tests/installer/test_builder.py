"""Fast rejection tests for Build-Installer.ps1; no compiler or installer is executed."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import unittest
import zipfile


@unittest.skipUnless(os.name == "nt", "Windows packaging tooling")
class BuilderGuards(unittest.TestCase):
    def setUp(self):
        self.repo = Path(__file__).resolve().parents[2]
        self.root = Path(tempfile.mkdtemp(prefix="Fyltrix-InstallerBuilder-"))
        self.release = self.root / "release"
        self.layout = self.release / "layout"
        (self.layout / "app-0.1.0").mkdir(parents=True)
        (self.layout / "Fyltrix.exe").write_bytes(b"launcher fixture")
        (self.layout / "app-0.1.0/Fyltrix.exe").write_bytes(b"payload fixture")
        (self.layout / "current.json").write_text('{"current":"0.1.0","previous":null}', encoding="utf-8")
        archive = self.release / "Fyltrix-0.1.0-win-x64.zip"
        with zipfile.ZipFile(archive, "w") as z:
            for path in self.layout.rglob("*"):
                if path.is_file():
                    z.write(path, path.relative_to(self.layout).as_posix())
        checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
        Path(str(archive) + ".sha256").write_text(checksum + "  " + archive.name, encoding="utf-8")
        (self.release / "build-info.json").write_text(json.dumps({"product": "Fyltrix", "runtime": "win-x64", "version": "0.1.0", "sha256": checksum}), encoding="utf-8")
        self.output = self.root / "output"

    def tearDown(self):
        # Remove any test-owned junction itself before deleting the test tree, never its target through the link.
        link = self.layout / "redirect"
        if link.exists():
            link.rmdir()
        assert self.root.resolve().parent == Path(tempfile.gettempdir()).resolve()
        assert self.root.name.startswith("Fyltrix-InstallerBuilder-")
        shutil.rmtree(self.root)

    def reject(self, message, output=None):
        result = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                                 str(self.repo / "tools/Build-Installer.ps1"), "-ReleaseDirectory", str(self.release),
                                 "-OutputDirectory", str(output or self.output), "-IsccPath", str(self.root / "must-not-run.exe")],
                                capture_output=True, text=True, errors="replace", timeout=30)
        self.assertNotEqual(0, result.returncode)
        self.assertIn(message, result.stderr)
        self.assertFalse(self.output.exists())

    def test_tampered_zip(self):
        with (self.release / "Fyltrix-0.1.0-win-x64.zip").open("ab") as stream:
            stream.write(b"changed")
        self.reject("ZIP/checksum/build record do not agree")

    def test_tampered_payload(self):
        (self.layout / "app-0.1.0/Fyltrix.exe").write_bytes(b"changed")
        self.reject("differs from its release ZIP")

    def test_extra_profile_file(self):
        (self.layout / "settings.json").write_text('{"private":"must not be packaged"}', encoding="utf-8")
        self.reject("Unexpected files were added")

    def test_wrong_pointer(self):
        (self.layout / "current.json").write_text('{"current":"1.0.0"}', encoding="utf-8")
        self.reject("exact standalone release")

    def test_protected_development_output(self):
        self.reject("output inside the checkout", self.repo / "artifacts/Fyltrix")

    def test_protected_profile_output(self):
        self.reject("must not overlap user data", Path(os.environ["APPDATA"]) / "FileLens")

    def test_nested_layout_output(self):
        self.reject("separate from the source layout", self.layout / "installer")

    def test_existing_destination(self):
        self.output.mkdir()
        sentinel = self.output / "keep.txt"
        sentinel.write_text("existing output", encoding="utf-8")
        result = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                                 str(self.repo / "tools/Build-Installer.ps1"), "-ReleaseDirectory", str(self.release),
                                 "-OutputDirectory", str(self.output)], capture_output=True, text=True, timeout=30)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("already exists", result.stderr)
        self.assertEqual("existing output", sentinel.read_text(encoding="utf-8"))

    def test_source_junction(self):
        target = self.root / "outside-source"
        target.mkdir()
        link = self.layout / "redirect"
        # Only generated temporary paths, passed as escaped PowerShell literals.
        script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + str(link).replace("'", "''") + "' -Target '" + str(target).replace("'", "''") + "' | Out-Null"
        import base64
        subprocess.run(["powershell", "-NoProfile", "-EncodedCommand", base64.b64encode(script.encode("utf-16le")).decode()], check=True, capture_output=True)
        self.reject("Source layout junctions/symlinks are not allowed")


if __name__ == "__main__":
    unittest.main(verbosity=2)
