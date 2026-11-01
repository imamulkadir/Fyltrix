"""Release tooling tests. Temporary inputs only; never call Git writes or GitHub."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
POWERSHELL = str(Path(os.environ.get('SystemRoot', r'C:\Windows')) / 'System32/WindowsPowerShell/v1.0/powershell.exe')


def desktop_environment():
    return {key: value for key, value in os.environ.items() if key.upper() != 'PSMODULEPATH'}


@unittest.skipUnless(os.name == 'nt', 'Windows PowerShell release tooling')
class ReleaseTools(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='Fyltrix-ReleaseTools-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'notes & unicode বাংলা'
        self.root.mkdir()
        self.notes = self.root / 'CHANGELOG.md'
        self.project = self.root / 'App.csproj'
        self.notes.write_text('# Changelog\n\n## [Unreleased]\n\n## [0.2.0] - 2026-11-01\n\n### Fixed\n\n- Preserve settings.\n\n## [0.1.0]\n\n- Earlier release.\n', encoding='utf-8')
        self.project.write_text('<Project><PropertyGroup><Version>0.2.0</Version><AssemblyVersion>0.2.0.0</AssemblyVersion><FileVersion>0.2.0.0</FileVersion></PropertyGroup></Project>')

    def run_script(self, script, *args):
        before = {p.name: p.read_bytes() for p in self.root.iterdir()}
        result = subprocess.run([POWERSHELL, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', str(REPO / 'tools' / script), *map(str, args)], capture_output=True, timeout=20, env=desktop_environment())
        self.assertEqual(before, {p.name: p.read_bytes() for p in self.root.iterdir()})
        return result

    def validate(self, tag='v0.2.0'):
        return self.run_script('Test-Release.ps1', '-Tag', tag, '-ProjectPath', self.project, '-ChangelogPath', self.notes)

    def test_matching_version_and_notes_succeed_without_changing_inputs(self):
        self.assertEqual(0, self.validate().returncode)

    def test_extraction_stops_before_the_next_version(self):
        result = self.run_script('Get-ReleaseNotes.ps1', '-Version', '0.2.0', '-Path', self.notes)
        self.assertEqual(0, result.returncode)
        self.assertIn(b'Preserve settings.', result.stdout)
        self.assertNotIn(b'Earlier release', result.stdout)

    def test_missing_duplicate_and_empty_sections_fail(self):
        for body in ('## [0.1.0]\n- old', '## [0.2.0]\n- first\n## [0.2.0]\n- second', '## [0.2.0]\n\n## [0.1.0]\n- old', '## [0.2.0]\n### Fixed\n<!-- TODO -->'):
            with self.subTest(body=body):
                self.notes.write_text(body)
                self.assertNotEqual(0, self.validate().returncode)

    def test_invalid_tags_and_windows_version_overflow_fail(self):
        for tag in ('0.2.0', 'v00.2.0', 'v0.2.0-beta.1', 'v0.2.0+build', 'v0.2.0;echo BAD', 'v65535.0.0'):
            with self.subTest(tag=tag):
                self.assertNotEqual(0, self.validate(tag).returncode)

    def test_mismatching_or_ambiguous_versions_fail(self):
        baseline = self.project.read_text()
        for text in (baseline.replace('0.2.0.0', '0.1.0.0', 1), baseline.replace('<Version>0.2.0</Version>', '<Version>0.1.0</Version>'), baseline.replace('</PropertyGroup>', '<Version>0.2.0</Version></PropertyGroup>'), baseline.replace('<PropertyGroup>', '<PropertyGroup Condition="true">')):
            with self.subTest(text=text):
                self.project.write_text(text)
                self.assertNotEqual(0, self.validate().returncode)

    def test_headings_inside_code_fences_are_not_sections(self):
        self.notes.write_text('```markdown\n## [0.2.0]\n- not a section\n```\n\n## [0.2.0]\n- actual\n```text\n## [0.1.0]\nexample\n```\n## [0.1.0]\n- old\n')
        result = self.run_script('Get-ReleaseNotes.ps1', '-Version', '0.2.0', '-Path', self.notes)
        self.assertEqual(0, result.returncode)
        self.assertIn(b'example', result.stdout)
        self.assertNotIn(b'- old', result.stdout)

    def test_unclosed_fence_and_oversized_notes_fail(self):
        for text in ('## [0.2.0]\n```\nunclosed', '## [0.2.0]\n' + 'x' * 16385):
            with self.subTest(text=text[:30]):
                self.notes.write_text(text)
                self.assertNotEqual(0, self.validate().returncode)

    def test_oversized_changelog_fails(self):
        self.notes.write_bytes(b'x' * (1024 * 1024 + 1))
        self.assertNotEqual(0, self.validate().returncode)

    def test_final_section_with_no_body_does_not_reuse_its_heading(self):
        self.notes.write_text('## [0.2.0]\n')
        self.assertNotEqual(0, self.validate().returncode)

    def test_default_paths_work_from_another_working_directory(self):
        version = ET.parse(REPO / 'src/Fyltrix/Fyltrix.csproj').find('./PropertyGroup/Version').text
        result = subprocess.run([POWERSHELL, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', str(REPO / 'tools/Test-Release.ps1'), '-Tag', 'v'+version], cwd=self.root, capture_output=True, timeout=20, env=desktop_environment())
        self.assertEqual(0, result.returncode, result.stderr.decode(errors='replace'))

    def test_completion_refuses_bad_tag_before_external_actions(self):
        result = self.run_script('Complete-Release.ps1', '-Tag', 'v0.2.0;invalid', '-KeyPath', self.root/'nonexistent.pem', '-PublicKeyPath', self.root/'nonexistent.txt', '-Publish')
        self.assertNotEqual(0, result.returncode)
        self.assertIn(b'Tag must', result.stderr)

    def test_version_preparation_changes_only_the_three_fields(self):
        original = self.project.read_text()
        self.notes.write_text('## [0.3.0]\n- Next tested release.\n')
        notes = self.notes.read_bytes()
        result = subprocess.run([POWERSHELL, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', str(REPO/'tools/Set-ReleaseVersion.ps1'), '-Version', '0.3.0', '-ProjectPath', str(self.project), '-ChangelogPath', str(self.notes)], capture_output=True, timeout=20, env=desktop_environment())
        self.assertEqual(0, result.returncode, result.stderr.decode(errors='replace'))
        self.assertEqual(original.replace('0.2.0', '0.3.0'), self.project.read_text())
        self.assertEqual(notes, self.notes.read_bytes())
        self.assertEqual({'App.csproj','CHANGELOG.md'}, {p.name for p in self.root.iterdir()})

    def test_version_preparation_refuses_missing_notes_and_downgrades_without_writes(self):
        for version,notes in [('0.3.0','## [0.2.0]\n- old'),('0.1.0','## [0.1.0]\n- older'),('0.2.0','## [0.2.0]\n- current')]:
            with self.subTest(version=version):
                self.notes.write_text(notes)
                result = self.run_script('Set-ReleaseVersion.ps1', '-Version', version, '-ProjectPath', self.project, '-ChangelogPath', self.notes)
                self.assertNotEqual(0, result.returncode)

    def test_core_module_paths_do_not_leak_into_desktop_release_checks(self):
        marker = str(self.root / 'core-only-module-path')
        with mock.patch.dict(os.environ, {'PSModulePath': marker}):
            self.assertNotIn('PSMODULEPATH', {key.upper() for key in desktop_environment()})
            self.assertEqual(0, self.validate().returncode)


if __name__ == '__main__':
    unittest.main()
