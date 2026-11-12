<#
.SYNOPSIS
    Regenerates Epic-Games_Account_Switcher-Globals\Strings.resx from the source code.

.DESCRIPTION
    Every user-facing text goes through L.T("English text", args...) in C# or {local:T 'English text'} in XAML.
    The English text is the resource key. This script collects all of them and writes Strings.resx (key = value = English),
    which is the template translators copy to Strings.<culture>.resx (for example Strings.de.resx) and translate.

    Run it after adding or changing texts:   powershell -ExecutionPolicy Bypass -File developer_tools\Extract-Strings.ps1
    -Check only reports whether Strings.resx is up to date (exit code 1 when it is not) and changes nothing.

    Rules for developers: the first argument of L.T(...) must be ONE string literal (no "a" + "b", no variables);
    use {0}, {1} placeholders for values.
#>
param([switch]$Check)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$resx = Join-Path $repo 'Epic-Games_Account_Switcher-Globals\Strings.resx'
$sourceDirs = @('Epic-Games_Account_Switcher-Client', 'Epic-Games_Account_Switcher-Globals') | ForEach-Object { Join-Path $repo $_ }

$keys = New-Object 'System.Collections.Generic.SortedSet[string]' ([System.StringComparer]::Ordinal)
$notLiteral = @()

foreach ($dir in $sourceDirs) {
    $files = Get-ChildItem $dir -Recurse -Include *.cs, *.xaml | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    foreach ($file in $files) {
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($file.Extension -eq '.cs') {
            foreach ($m in [regex]::Matches($text, '\bL\.T\(\s*"((?:[^"\\]|\\.)*)"')) {
                [void]$keys.Add([regex]::Unescape($m.Groups[1].Value))
            }
            # L.T( followed by anything but a literal cannot be extracted (L.T(T(text)) inside L.cs itself is fine).
            if ($file.Name -ne 'L.cs') {
                $lines = $text -split "`n"
                foreach ($m in [regex]::Matches($text, '\bL\.T\(\s*[^"\s]')) {
                    $line = ($text.Substring(0, $m.Index) -split "`n").Count
                    # A line marked "// extractor: passthrough" forwards a text that was already extracted elsewhere.
                    if ($lines[$line - 1] -match '//\s*extractor:\s*passthrough') { continue }
                    $notLiteral += ('{0}:{1}' -f $file.Name, $line)
                }
            }
        }
        else {
            foreach ($m in [regex]::Matches($text, "\{local:T\s+'((?:[^'\\]|\\.)*)'\}")) {
                $value = [regex]::Replace($m.Groups[1].Value, '\\(.)', '$1')
                [void]$keys.Add([System.Net.WebUtility]::HtmlDecode($value))
            }
        }
    }
}

if ($notLiteral.Count -gt 0) {
    Write-Host 'L.T(...) must start with one string literal. Fix:' -ForegroundColor Red
    $notLiteral | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 2
}

# Build the file in memory so -Check can compare without touching the disk.
$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.Encoding = New-Object System.Text.UTF8Encoding $false
$stream = New-Object System.IO.MemoryStream
$writer = [System.Xml.XmlWriter]::Create($stream, $settings)
$writer.WriteStartDocument()
$writer.WriteStartElement('root')
foreach ($header in @(
        @('resmimetype', 'text/microsoft-resx'),
        @('version', '2.0'),
        @('reader', 'System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'),
        @('writer', 'System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'))) {
    $writer.WriteStartElement('resheader'); $writer.WriteAttributeString('name', $header[0]); $writer.WriteElementString('value', $header[1]); $writer.WriteEndElement()
}
# Resource names are case-insensitive: texts that differ only in case share one entry (see L.cs). Keep the variant
# that is not all capitals so translators see normal sentence case.
$unique = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($key in $keys) {
    if (-not $unique.ContainsKey($key)) { $unique[$key] = $key; continue }
    if ($unique[$key] -ceq $unique[$key].ToUpperInvariant() -and $key -cne $key.ToUpperInvariant()) { $unique[$key] = $key }
}
$entries = New-Object 'System.Collections.Generic.SortedSet[string]' ([System.StringComparer]::Ordinal)
foreach ($value in $unique.Values) { [void]$entries.Add($value) }

foreach ($key in $entries) {
    $writer.WriteStartElement('data')
    $writer.WriteAttributeString('name', $key)
    $writer.WriteAttributeString('xml', 'space', 'http://www.w3.org/XML/1998/namespace', 'preserve')
    $writer.WriteElementString('value', $key)
    $writer.WriteEndElement()
}
$writer.WriteEndElement()
$writer.WriteEndDocument()
$writer.Flush()
$bytes = $stream.ToArray()
$writer.Dispose()
$generated = [System.Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF)

$current = if (Test-Path $resx) { [IO.File]::ReadAllText($resx) } else { '' }
$same = ($current.Replace("`r`n", "`n").Trim() -eq $generated.Replace("`r`n", "`n").Trim())

if ($Check) {
    if ($same) { Write-Host "Strings.resx is up to date ($($entries.Count) texts)."; exit 0 }
    Write-Host 'Strings.resx is out of date. Run developer_tools\Extract-Strings.ps1.' -ForegroundColor Yellow
    exit 1
}

if (-not $same) { [IO.File]::WriteAllText($resx, $generated, (New-Object System.Text.UTF8Encoding $false)) }
Write-Host ("Strings.resx: {0} texts{1}." -f $entries.Count, $(if ($same) { ' (unchanged)' } else { ' (updated)' }))
