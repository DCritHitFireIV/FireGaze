"""Exercise the real PowerShell gate's scheduling with only dotnet substituted.

This is a dispatch regression, never offline qualification evidence.
"""
from pathlib import Path
import subprocess
import tempfile
import unittest


class OfflineSuiteDispatchTests(unittest.TestCase):
    def test_full_gate_dispatches_every_current_project_once(self):
        repo = Path(__file__).resolve().parents[2]
        expected = sorted(path.stem for path in (repo / 'tools').glob('*Tests/*Tests.csproj'))
        with tempfile.TemporaryDirectory() as folder:
            fixture = Path(folder)
            sqpack = fixture / 'sqpack'
            sqpack.mkdir()
            compat = fixture / 'compat'
            (compat / 'Services').mkdir(parents=True)
            (compat / 'Services/LocalPreviewProjection.cs').write_text('// fixture', encoding='utf-8')
            harness = fixture / 'dispatch.ps1'
            harness.write_text('''param([string]$Gate,[string]$Sqpack,[string]$Compat)
function global:dotnet {
    $callArguments = @($args)
    $global:LASTEXITCODE = 0
    if ($callArguments[0] -eq 'run') {
        $projectIndex = [Array]::IndexOf($callArguments, '--project')
        if ($projectIndex -lt 0) { throw 'No project supplied' }
        Write-Output ('DISPATCHED:' + [IO.Path]::GetFileNameWithoutExtension([string]$callArguments[$projectIndex + 1]))
    }
}
& $Gate -SqpackPath $Sqpack -HmsCompatRoot $Compat -RequireFullValidation
''', encoding='utf-8')
            result = subprocess.run(['pwsh', '-NoProfile', '-File', str(harness),
                '-Gate', str(repo / 'build-local-preview.ps1'), '-Sqpack', str(sqpack),
                '-Compat', str(compat)], capture_output=True, text=True, encoding='utf-8', timeout=30)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        actual = [line.removeprefix('DISPATCHED:') for line in result.stdout.splitlines()
                  if line.startswith('DISPATCHED:')]
        self.assertEqual(expected, sorted(actual), 'Gate omitted or duplicated a current test project')
        self.assertEqual(len(actual), len(set(actual)))
        self.assertIn(f'Offline validation finished: {len(expected)} suites; skipped: .', result.stdout)


if __name__ == '__main__':
    unittest.main()
