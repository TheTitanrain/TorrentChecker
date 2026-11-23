param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

# Run with Windows PowerShell 5.1: the application targets .NET Framework 4.8.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Windows.Forms
$assemblyPath = Join-Path $PSScriptRoot "..\TorrentChecker\bin\$Configuration\TorrentChecker.exe"
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $assemblyPath))
$atom = $assembly.GetType('TorrentChecker.clsRuTrackerAtom', $true)
$staticFlags = [Reflection.BindingFlags]'Static,NonPublic'
$instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic'
$script:checks = 0

function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:checks++
}

$versionCheck = $assembly.GetType('TorrentChecker.frmMain', $true).GetMethod('IsNewerVersion', $staticFlags)
foreach ($case in @(
    @{ Candidate = '3.0.15'; Current = '3.0.17'; Expected = $false },
    @{ Candidate = '3.0.17'; Current = '3.0.17'; Expected = $false },
    @{ Candidate = '3.0.18'; Current = '3.0.17'; Expected = $true },
    @{ Candidate = '3.0.9'; Current = '3.0.17'; Expected = $false },
    @{ Candidate = '3.0.10'; Current = '3.0.9'; Expected = $true },
    @{ Candidate = '3.1.0'; Current = '3.0.17'; Expected = $true },
    @{ Candidate = '4.0.0'; Current = '3.9.99'; Expected = $true },
    @{ Candidate = '2.99.99'; Current = '3.0.17'; Expected = $false },
    @{ Candidate = '3.0.17.0'; Current = '3.0.17'; Expected = $false },
    @{ Candidate = '3.0.17'; Current = '3.0.17.0'; Expected = $false },
    @{ Candidate = '3.0'; Current = '3.0.0.0'; Expected = $false },
    @{ Candidate = '3.0.0.0'; Current = '3.0'; Expected = $false },
    @{ Candidate = '3.0.17.1'; Current = '3.0.17'; Expected = $true },
    @{ Candidate = ' v3.0.18 '; Current = ' V3.0.17 '; Expected = $true },
    @{ Candidate = 'V3.0.18'; Current = 'v3.0.17'; Expected = $true }
)) {
    $actual = $versionCheck.Invoke($null, @($case.Candidate, $case.Current))
    Assert ($actual -eq $case.Expected) "Version comparison: $($case.Candidate) vs $($case.Current)"
}
foreach ($invalid in @($null, '', ' ', 'abc', '3', 'vv3.0.18', 'v', '3.0.18-beta',
    '3.0.18+metadata', '3.-1.18', '3.0.2147483648', '3.0.18.0.1')) {
    Assert (-not $versionCheck.Invoke($null, @($invalid, '3.0.17'))) "Reject invalid candidate: $invalid"
    Assert (-not $versionCheck.Invoke($null, @('3.0.18', $invalid))) "Reject invalid current version: $invalid"
}

function Invoke-Atom([string]$name, $value) {
    # Preserve returned collections, including empty lists.
    return ,$atom.GetMethod($name, $staticFlags).Invoke($null, @($value))
}

function Get-EntryValue($entry, [string]$name) {
    return $atom.GetProperty($name, $instanceFlags).GetValue($entry)
}

foreach ($value in @('', '  ', '-1', $null)) {
    Assert ((Invoke-Atom ForumIds $value) -join ',' -eq '486') 'Legacy/default forum must map to 486'
}
Assert (((Invoke-Atom ForumIds '486, 123,486') -join ',') -eq '486,123') 'Trim and deduplicate forums'
foreach ($value in @('486', '1', '2147483647')) {
    Assert (Invoke-Atom IsForumId $value) "Valid forum rejected: $value"
}
foreach ($value in @('', '0', '-1', '1.5', 'abc', '../486', '2147483648')) {
    Assert (-not (Invoke-Atom IsForumId $value)) "Invalid forum accepted: $value"
}

$hash = '0123456789ABCDEF0123456789ABCDEF01234567'
$magnet = "magnet:?xt=urn:btih:$hash&dn=Alpine%20skiing"
Assert ((Invoke-Atom MagnetHash $magnet) -ceq $hash.ToLowerInvariant()) 'Normalize info hash'
foreach ($value in @('', 'https://example.org/', "magnet:?xt=urn:btih:abc", "$magnet#fragment", "$magnet bad", "$magnet`n", "$magnet`"")) {
    Assert ((Invoke-Atom MagnetHash $value) -eq '') "Unsafe magnet accepted: $value"
}

$xml = @"
<feed xmlns="http://www.w3.org/2005/Atom">
  <title>Winter sports</title>
  <entry>
    <id>https://rutracker.org/forum/t/123</id>
    <title>Alpine Skiing World Cup [1.25 GB]</title>
    <category label="Alpine skiing"/><author><name>Uploader</name></author>
    <link rel="alternate" href="https://example.org/"/>
    <link rel="enclosure" href="https://example.org/file.torrent"/>
    <link rel="enclosure" href="magnet:?xt=urn:btih:$hash&amp;dn=Alpine%20skiing"/>
    <updated>2026-11-16T00:00:00Z</updated>
  </entry>
  <entry><id>https://rutracker.org/forum/t/124</id><title>No magnet</title>
    <updated>2026-11-15T00:00:00Z</updated></entry>
  <entry><id>invalid</id><title>Skip invalid ID</title></entry>
  <entry><id>https://rutracker.org/forum/t/125</id><title> </title></entry>
</feed>
"@
$originalCulture = [Threading.Thread]::CurrentThread.CurrentCulture
try {
    [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]'ru-RU'
    $entries = Invoke-Atom Read $xml
    Assert ($entries.Count -eq 2) 'Skip entries without topic IDs or titles'
    $entry = $entries[0]
    Assert ((Get-EntryValue $entry TopicId) -eq '123') 'Read topic ID'
    Assert ((Get-EntryValue $entry Title) -eq 'Alpine Skiing World Cup') 'Strip size from title'
    Assert ((Get-EntryValue $entry Size) -eq 1.25) 'Parse size independently of locale'
    Assert ((Get-EntryValue $entry SizeUnit) -eq 'GB') 'Read size unit'
    Assert ((Get-EntryValue $entry Forum) -eq 'Alpine skiing') 'Read category'
    Assert ((Get-EntryValue $entry Author) -eq 'Uploader') 'Read author'
    Assert ((Get-EntryValue $entry Magnet) -ceq $magnet) 'Choose valid enclosure and decode XML entities'
    Assert ((Get-EntryValue $entry InfoHash) -ceq $hash.ToLowerInvariant()) 'Use info hash for update detection'
    $matches = $atom.GetMethod('Matches', $instanceFlags)
    Assert ($matches.Invoke($entry, @('  SKIING world  '))) 'Match phrase without case or surrounding spaces'
    Assert ($matches.Invoke($entry, @(''))) 'Empty phrase matches every entry'
    Assert (-not $matches.Invoke($entry, @('skiing.*cup'))) 'Search phrase is literal, not a regex'
    $missing = $entries[1]
    Assert ((Get-EntryValue $missing Forum) -eq 'Winter sports') 'Fall back to feed title'
    Assert ((Get-EntryValue $missing Author) -eq '') 'Allow missing author'
    Assert ((Get-EntryValue $missing Magnet) -eq '') 'Allow missing magnet'
    Assert ((Get-EntryValue $missing SizeUnit) -eq '') 'Do not invent missing size'
    Assert ((Get-EntryValue $missing InfoHash) -eq 'atom:2026-11-15T00:00:00Z') 'Use updated timestamp without magnet'
} finally {
    [Threading.Thread]::CurrentThread.CurrentCulture = $originalCulture
}
Assert ((Invoke-Atom Read '<feed xmlns="http://www.w3.org/2005/Atom"/>').Count -eq 0) 'Accept empty feed'
foreach ($invalidXml in @('<html/>', '<feed/>', '<feed')) {
    $rejected = $false
    try { $null = Invoke-Atom Read $invalidXml } catch { $rejected = $true }
    Assert $rejected 'Reject non-Atom or malformed responses'
}

$uiText = $assembly.GetType('TorrentChecker.UiText', $true)
$selectLanguage = $uiText.GetMethod('SelectLanguage', $staticFlags)
foreach ($resource in $assembly.GetManifestResourceNames() -clike '*.tsv') {
    $reader = New-Object IO.StreamReader($assembly.GetManifestResourceStream($resource))
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            $fields = $line.Split([char]9)
            Assert ($fields.Count -eq 5 -and $fields -notcontains '') "Incomplete translation in $resource"
            $tokens = [regex]::Matches($fields[0], '\{\d+\}|%[A-Za-z]+%') | ForEach-Object { $_.Value } | Sort-Object
            foreach ($field in $fields[1..4]) {
                $translatedTokens = [regex]::Matches($field, '\{\d+\}|%[A-Za-z]+%') | ForEach-Object { $_.Value } | Sort-Object
                Assert (($tokens -join ',') -ceq ($translatedTokens -join ',')) "Changed placeholder in $field"
            }
        }
    } finally { $reader.Dispose() }
}
$saveLabels = @{'ru'='Сохранить'; 'en'='Save'; 'uk'='Зберегти'; 'kk'='Сақтау'; 'be'='Захаваць'}
foreach ($code in $saveLabels.Keys) {
    $selectLanguage.Invoke($null, @($code))
    $form = [Activator]::CreateInstance($assembly.GetType('TorrentChecker.frmAppOptions', $true))
    try {
        Assert ($form.Controls.Find('btnSave', $true)[0].Text -ceq $saveLabels[$code]) "Options button in $code"
    } finally { $form.Dispose() }
}
$activeCode = $uiText.GetProperty('ActiveCode', $staticFlags)
$thread = [Threading.Thread]::CurrentThread
$savedCulture = $thread.CurrentUICulture
try {
    $thread.CurrentUICulture = [Globalization.CultureInfo]::GetCultureInfo('uk-UA')
    $selectLanguage.Invoke($null, @('auto'))
    Assert ($activeCode.GetValue($null) -ceq 'uk') 'System mode follows the UI culture'
    $selectLanguage.Invoke($null, @('en'))
    Assert ($activeCode.GetValue($null) -ceq 'en') 'Explicit language overrides the system culture'
    $thread.CurrentUICulture = [Globalization.CultureInfo]::GetCultureInfo('fr-FR')
    $selectLanguage.Invoke($null, @('auto'))
    Assert ($activeCode.GetValue($null) -ceq 'ru') 'Unsupported system culture falls back to Russian'
} finally { $thread.CurrentUICulture = $savedCulture }

$form = [Activator]::CreateInstance($assembly.GetType('TorrentChecker.frmMain', $true))
try {
    $search = $form.Controls.Find('txtSearch', $true)[0]
    $search.Tag = $null
    $search.Text = 'Ubuntu 24'
    $refresh = $form.GetType().GetMethod('RefreshLanguage', $instanceFlags)
    $tab = $form.GetType().GetProperty('TabPage_journal', $instanceFlags).GetValue($form)
    foreach ($case in @(@('en','Error log'), @('be','Журнал памылак'))) {
        $selectLanguage.Invoke($null, @($case[0]))
        $refresh.Invoke($form, @())
        Assert ($tab.Text -ceq $case[1]) 'Existing main window reflects the new language'
        Assert ($search.Text -ceq 'Ubuntu 24') 'Refreshing the language preserves search input'
    }
} finally { $form.Dispose() }
foreach ($code in @('ru', 'en', 'uk', 'kk', 'be')) {
    $selectLanguage.Invoke($null, @($code))
    $options = [Activator]::CreateInstance($assembly.GetType('TorrentChecker.frmAppOptions', $true))
    try {
        $caption = $options.Controls.Find('Label3', $true)[0]
        $field = $options.Controls.Find('txtTorrentsStoreTime', $true)[0]
        Assert (($caption.Left + $caption.PreferredSize.Width) -lt $field.Left) "Retention caption fits before the days field ($code)"
    } finally { $options.Dispose() }
}
$selectLanguage.Invoke($null, @('ru'))
$form = [Activator]::CreateInstance($assembly.GetType('TorrentChecker.frmAppOptions', $true))
try {
    $form.ShowInTaskbar = $false
    $form.Opacity = 0
    $form.Show()
    $choice = $form.Controls.Find('languageChoice', $true)[0]
    Assert ($choice.SelectedIndex -eq 0 -and $choice.Text -ceq 'Язык системы') 'Options restore the current language on open'
    $choice.SelectedItem = 'English'
    Assert ($choice.Text -ceq 'English') 'Select an explicit language before cancelling'
    $form.Controls.Find('btnCancel', $true)[0].PerformClick()
    $form.Show()
    Assert ($choice.SelectedIndex -eq 0 -and $choice.Text -ceq 'Язык системы') 'Cancel discards the pending language selection'
} finally { $form.Dispose() }

# Drive the options menu and its modal Save button. Replace the registry/disk write
# with one failure followed by success so the test cannot change user settings.
$main = [Activator]::CreateInstance($assembly.GetType('TorrentChecker.frmMain', $true))
$project = $assembly.GetType('TorrentChecker.My.MyProject', $true)
$forms = $project.GetProperty('Forms', $staticFlags).GetValue($null)
$optionsProperty = $forms.GetType().GetProperty('frmAppOptions')
$dialog = $optionsProperty.GetValue($forms)
$optionsField = $assembly.GetType('TorrentChecker.mdlMain').GetField('AppOptions')
$options = $optionsField.GetValue($null)
$backup = New-Object 'System.Collections.Generic.Dictionary[string,object]' (,$options)
$modalResult = @{ Error = $null; Writes = 0 }
try {
    $search = $main.Controls.Find('txtSearch', $true)[0]
    $search.Text = 'Linux'
    $dialog.GetType().GetField('PersistOptions', $instanceFlags).SetValue($dialog, [Func[bool]]{
        $modalResult.Writes++
        return $modalResult.Writes -eq 2
    })
    $dialog.add_Shown({
        try {
            $list = $dialog.Controls.Find('languageChoice', $true)[0]
            $list.SelectedItem = 'English'
            $save = $dialog.Controls.Find('btnSave', $true)[0]
            $save.PerformClick()
            Assert ($dialog.Visible -and $options['language'] -ceq 'auto' -and $activeCode.GetValue($null) -ceq 'ru') 'Failed save preserves active language and preference'
            $save.PerformClick()
        } catch { $modalResult.Error = $_ } finally { $dialog.Hide() }
    })
    $main.GetType().GetMethod('msiAppOptions_Click', $instanceFlags).Invoke($main, @($null, [EventArgs]::Empty))
    if ($modalResult.Error) { throw $modalResult.Error }
    Assert ($modalResult.Writes -eq 2 -and $options['language'] -ceq 'en') 'Retry commits the explicit language'
    Assert ($main.Controls.Find('btnConnect', $true)[0].Text -ceq 'Check now' -and $search.Text -ceq 'Linux') 'Saving through the dialog refreshes the existing window'
    $reopened = $optionsProperty.GetValue($forms)
    Assert ($reopened.Controls.Find('btnSave', $true)[0].Text -ceq 'Save') 'Reopened options use the saved language'
} finally {
    $optionsProperty.SetValue($forms, $null)
    $optionsField.SetValue($null, $backup)
    $main.Dispose()
    $selectLanguage.Invoke($null, @('ru'))
}

# Construct windows without showing them or running Load handlers. This verifies
# compiled WinForms resources without network requests or user settings writes.
foreach ($name in @('frmMain', 'frmKeyWord', 'frmCredentials', 'frmForums', 'frmAppOptions')) {
    $form = [Activator]::CreateInstance($assembly.GetType("TorrentChecker.$name", $true))
    try {
        Assert ($form.Controls.Count -gt 0) "Window resources did not load: $name"
        if ($name -eq 'frmMain') {
            $menu = $form.GetType().GetField('openMagnetMenu', $instanceFlags).GetValue($form)
            Assert ($null -ne $menu.Owner) 'Magnet action must be attached to the context menu'
        } elseif ($name -eq 'frmCredentials') {
            $passwordBox = $form.Controls.Find('txtProxyPassword', $true)[0]
            Assert $passwordBox.UseSystemPasswordChar 'Proxy password input must be masked'
        }
    } finally { $form.Dispose() }
}
Assert ($assembly.EntryPoint.DeclaringType.FullName -eq 'TorrentChecker.mdlStartup') 'Use startup error logging entry point'

$storeType = $assembly.GetType('TorrentChecker.clsSettingsStore', $true)
$jsonType = $assembly.GetType('TorrentChecker.clsSettingsJson', $true)
$vaultType = $assembly.GetType('TorrentChecker.clsCredentialStore', $true)
function New-Store([string]$root) {
    return $storeType.GetConstructors($instanceFlags)[0].Invoke(@($root))
}
function Invoke-Store($store, [string]$method, [object[]]$arguments) {
    if ($null -ne $arguments) {
        for ($i = 0; $i -lt $arguments.Length; $i++) {
            if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject }
        }
    }
    if ($method -eq 'Load' -and $arguments.Length -eq 1) {
        $arguments = @($arguments[0], $null)
    }
    return ,$storeType.GetMethod($method, $instanceFlags).Invoke($store, $arguments)
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('TorrentChecker-test-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
$store = New-Store $root
$settingsPath = Join-Path $root 'settings.json'
$legacyPath = Join-Path $root 'settings.dat'
$accept = [Func[bool]]{ $true }
$reject = [Func[bool]]{ $false }
$unexpected = [Func[bool]]{ throw 'Legacy import should not be offered' }
$lock = $null
$manifest = $null
try {
    $lock = Invoke-Store $store AcquireLock
    $locked = $false
    try { $null = Invoke-Store (New-Store $root) AcquireLock } catch { $locked = $true }
    Assert $locked 'Only one process or store can hold the directory lock'
    Assert ($null -eq (Invoke-Store $store Load @($unexpected))) 'Fresh installation has no stored settings'
    [IO.File]::WriteAllBytes($legacyPath, [byte[]]@())
    Assert ($null -eq (Invoke-Store $store Load @($unexpected))) 'Empty legacy file is a fresh configuration'

    $fixture = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    $rules = New-Object 'System.Collections.Generic.Dictionary[int,System.Collections.Generic.Dictionary[string,string]]'
    $legacyClientCommand = '%ClientPath% /directory "C:\PathToSave\%TorrentName%" %TorrentPath%'
    $newClientCommand = $legacyClientCommand.Replace('%TorrentPath%', '%TorrentSource%')
    $literalClientCommand = '"C:\Program Files\Client\client.exe" --start'
    foreach ($id in @(0, 7, 2, 9)) {
        $rule = New-Object 'System.Collections.Generic.Dictionary[string,string]'
        $rule.Add('label', ('Rule ' + $id + ' { "quote" \\ slash }'))
        $rule.Add('count_of_new', $(if ($id -eq 2) { '-1' } else { '2' }))
        $rule.Add('paused', 'False')
        if ($id -ne 0) {
            $rule.Add('rutracker', ($id -ne 9).ToString())
            $rule.Add('kinozal', ($id -ne 2).ToString())
            $rule.Add('client_command_line', $(if ($id -eq 9) { $literalClientCommand } else { $legacyClientCommand }))
        }
        $rules.Add($id, $rule)
    }
    $fixture.Add('KeyWordsParams', $rules)
    $fixture.Add('KwListToParamsMapping', [int[]]@(0, 7, 2, 9))
    $fixture.Add('CHECK_INTERVAL', 29000)
    $fixture.Add('TORRENTS_MAX_RESULTS', [int16]200)
    $fixture.Add('TORRENTS_MAX_STORETIME', [long]20000000001)
    $window = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    $window.Add('width', 1234)
    $window.Add('windowstate', [Windows.Forms.FormWindowState]::Maximized)
    $fixture.Add('frmMain', $window)
    $column = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    $column.Add('width', 451)
    $column.Add('visibility', $false)
    $columns = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    $columns.Add('topic_name', $column)
    $fixture.Add('dgvTorrentsColumns', $columns)
    $proxy = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    $proxy.Add('use_proxy', $true)
    $proxy.Add('proxy_address', 'localhost')
    $proxy.Add('proxy_port', '3128')
    $fixture.Add('proxy', $proxy)
    $user = 'test-' + [char]0x416
    $rtSecret = 'rt-test-secret-' + [char]0x417
    $kzSecret = 'kz-test-secret-' + [char]0x418
    $proxySecret = 'proxy-test-secret-' + [char]0x419
    $ru = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    $ru.Add('login_username', $user)
    $ru.Add('login_password', $rtSecret)
    $fixture.Add('rutracker_credentials', $ru)
    $kz = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    $kz.Add('username', $user)
    $kz.Add('password', $kzSecret)
    $fixture.Add('kinozal_credentials', $kz)
    $options = New-Object 'System.Collections.Generic.Dictionary[string,object]'
    $options.Add('check_for_updates', $false)
    $options.Add('dgv_font_bold', (New-Object Drawing.Font('Verdana', 11, [Drawing.FontStyle]'Bold,Italic')))
    $fixture.Add('AppOptions', $options)

    $schema = $jsonType.GetMethod('CreateHistoryTable', $staticFlags)
    [Data.DataTable]$found = $schema.Invoke($null, @($true))
    [Data.DataTable]$stored = $schema.Invoke($null, @($false))
    $time = [long]9007199254740993
    $row = $found.NewRow()
    $row['tracker_id'] = 1
    $row['found_unix_datetime'] = $time
    $row['keyword_id'] = 7
    $row['marked_as_new'] = $true
    $row['torrent_size_bytes'] = 18.125
    $found.Rows.Add($row)
    $row = $found.NewRow()
    $row['keyword_id'] = 2
    $row['topic_name'] = 'Second rule result'
    $found.Rows.Add($row)
    $null = $stored.Rows.Add([object[]]@(1, $time, '123', 'abc'))
    $fixture.Add('dtFoundTorrents', $found)
    $fixture.Add('dtStoredTorrents', $stored)

    $stream = [IO.File]::Create($legacyPath)
    try {
        $gzip = New-Object IO.Compression.GZipStream($stream, [IO.Compression.CompressionMode]::Compress)
        try { (New-Object Runtime.Serialization.Formatters.Binary.BinaryFormatter).Serialize($gzip, $fixture) }
        finally { $gzip.Dispose() }
    } finally { $stream.Dispose() }
    $legacyHash = (Get-FileHash -LiteralPath $legacyPath).Hash
    $declined = $false
    try { $null = Invoke-Store $store Load @($reject) } catch { $declined = $_.Exception.InnerException -is [OperationCanceledException] }
    Assert ($declined -and -not (Test-Path -LiteralPath $settingsPath)) 'Declining legacy import leaves JSON unpublished'

    # Block the final rename so the history and vault staging actually execute before the failure.
    $blockImport = [Func[bool]]{
        $null = [IO.Directory]::CreateDirectory($settingsPath)
        return $true
    }
    $failed = $false
    try { $null = Invoke-Store $store Load @($blockImport) } catch {
        if ($_.Exception.InnerException -isnot [ComponentModel.Win32Exception]) { throw }
        $failed = $true
    }
    Assert ($failed -and @(Get-ChildItem -Path $root -Filter 'history-*.json').Count -eq 0) 'Failed import removes staged history'
    [IO.Directory]::Delete($settingsPath)

    $data = Invoke-Store $store Load @($accept)
    $manifest = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    Assert ($data['AppOptions']['language'] -ceq 'auto') 'Legacy settings default to system language'
    $initialManifest = $manifest
    Assert ((Get-FileHash -LiteralPath $legacyPath).Hash -eq $legacyHash) 'Legacy migration preserves original bytes'
    Assert (($data['KwListToParamsMapping'] -join ',') -eq '0,7,2,9') 'Preserve rule IDs and display order'
    Assert ($data['KeyWordsParams'][7]['label'] -ceq $rules[7]['label']) 'JSON preserves quotes, braces and backslashes in rule text'
    Assert ($data['KeyWordsParams'][7]['client_command_template'] -ceq $newClientCommand -and
            $data['KeyWordsParams'][2]['client_command_template'] -ceq '' -and
            $data['KeyWordsParams'][9]['client_command_template'] -ceq $literalClientCommand) 'Legacy import migrates mixed, RuTracker-only and Kinozal-only commands'
    Assert ($manifest.rules.'7'.client_command_template -ceq $newClientCommand -and
            $rules[7]['client_command_line'] -ceq $legacyClientCommand) 'Legacy conversion writes normalized command without changing original rules'
    $oldJson = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $oldJson.options.PSObject.Properties.Remove('language')
    foreach ($id in @(7, 2, 9)) {
        $rule = $oldJson.rules.PSObject.Properties[[string]$id].Value
        $rule.PSObject.Properties.Remove('client_command_template')
        Add-Member -InputObject $rule -NotePropertyName client_command_line -NotePropertyValue ($rules[$id]['client_command_line'].Replace('%TorrentPath%', '%torrentpath%'))
    }
    [IO.File]::WriteAllText($settingsPath, ($oldJson | ConvertTo-Json -Depth 30))
    $fromOldJson = Invoke-Store (New-Store $root) Load @($unexpected)
    Assert ($fromOldJson['KeyWordsParams'][7]['client_command_template'] -ceq $newClientCommand -and
            $fromOldJson['KeyWordsParams'][2]['client_command_template'] -ceq '' -and
            $fromOldJson['KeyWordsParams'][9]['client_command_template'] -ceq $literalClientCommand) 'Old JSON migrates all rule types and legacy token casing'
    Assert ($fromOldJson['AppOptions']['language'] -ceq 'auto') 'Missing language in old JSON defaults to system language'
    Add-Member -InputObject $oldJson.options -NotePropertyName language -NotePropertyValue 'unknown'
    [IO.File]::WriteAllText($settingsPath, ($oldJson | ConvertTo-Json -Depth 30))
    $unknownLanguage = Invoke-Store (New-Store $root) Load @($unexpected)
    Assert ($unknownLanguage['AppOptions']['language'] -ceq 'auto') 'Unsupported UI language defaults to system language'
    $data['AppOptions']['language'] = 'kk'
    $data['KeyWordsParams'][2]['client_command_template'] = '%ClientPath% %TorrentSource%'
    $data['KeyWordsParams'][7]['client_command_template'] = ''
    Assert ($data['KeyWordsParams'][2]['count_of_new'] -eq '-1') 'Preserve signed legacy unread counts'
    Assert ($data['AppOptions']['dgv_font_bold'].Size -eq 11 -and $data['AppOptions']['dgv_font_bold'].Italic) 'Restore legacy font'
    Assert ($data['frmMain']['width'] -eq 1234 -and $data['frmMain']['windowstate'] -eq [Windows.Forms.FormWindowState]::Maximized -and
            $data['dgvTorrentsColumns']['topic_name']['width'] -eq 451 -and
            -not $data['dgvTorrentsColumns']['topic_name']['visibility']) 'Restore window and grid layout'
    Assert ($data['TORRENTS_MAX_STORETIME'] -eq 20000000001 -and $data['TORRENTS_MAX_RESULTS'] -eq 200 -and
            $data['CHECK_INTERVAL'] -eq 29000) 'Preserve intervals, retention and result limit'
    Assert ($data['rutracker_credentials']['login_password'] -ceq $rtSecret -and
            $data['rutracker_credentials']['login_username'] -ceq $user -and $data['rutracker_credentials'].ContainsKey('login')) 'Restore Unicode RuTracker credentials and protocol defaults'
    Assert ($data['kinozal_credentials']['password'] -ceq $kzSecret -and $data['kinozal_credentials'].ContainsKey('wact')) 'Restore Kinozal password and protocol defaults'
    Assert ($data['proxy']['proxy_port'] -eq '3128' -and $data['proxyCredential'].Password -eq '') 'Migrate unauthenticated legacy proxy'
    $row = $data['dtFoundTorrents'].Rows[0]
    Assert ($row['keyword_id'] -eq 7 -and $row['found_unix_datetime'] -eq $time -and $row['tracker_id'] -eq 1 -and
            $row['marked_as_new'] -eq $true -and $row['torrent_size_bytes'] -eq 18.125 -and
            $row.IsNull('topic_name')) 'Restore found history types and nulls'
    Assert ($data['dtStoredTorrents'].Rows[0]['found_unix_datetime'] -eq $time) 'Restore deduplication history precisely'
    Assert ($data['dtFoundTorrents'].Rows[1]['keyword_id'] -eq 2 -and
            $data['dtFoundTorrents'].Rows[1]['topic_name'] -eq 'Second rule result') 'Keep results associated with both reordered rules'

    $data['proxyCredential'] = (New-Object Net.NetworkCredential($user, $proxySecret)).PSObject.BaseObject
    $beforeSave = [IO.File]::ReadAllText($settingsPath)
    $blocked = [IO.File]::Open($settingsPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $failed = $false
        try { $null = Invoke-Store $store Save @($data, $true) } catch {
            if ($_.Exception.InnerException -isnot [ComponentModel.Win32Exception]) { throw }
            $failed = $true
        }
        Assert $failed 'An unavailable manifest must fail the save'
    } finally { $blocked.Dispose() }
    $reloaded = Invoke-Store (New-Store $root) Load @($unexpected)
    Assert ($reloaded['rutracker_credentials']['login_password'] -ceq $rtSecret -and
            $reloaded['proxyCredential'].Password -eq '' -and [IO.File]::ReadAllText($settingsPath) -ceq $beforeSave -and
            $reloaded['dtFoundTorrents'].Rows[0]['keyword_id'] -eq 7 -and
            @(Get-ChildItem -Path $root -Filter 'history-*.json').Count -eq 1) 'Failed save retains prior settings, history and vault records'
    $null = Invoke-Store $store Save @($data, $true)
    $rawJson = Get-Content -LiteralPath $settingsPath -Raw
    $manifest = $rawJson | ConvertFrom-Json
    $historyPath = Join-Path $root ('history-' + $manifest.historyId + '.json')
    foreach ($secret in @($rtSecret, $kzSecret, $proxySecret)) {
        Assert (-not $rawJson.Contains($secret) -and -not [IO.File]::ReadAllText($historyPath).Contains($secret)) 'Secrets are absent from both JSON files'
    }
    Assert ($rawJson.Contains([Environment]::NewLine) -and -not $rawJson.Contains('dtFoundTorrents')) 'Settings are formatted and omit history tables'
    $reloaded = Invoke-Store (New-Store $root) Load @($unexpected)
    $startupLanguage = [Delegate]::CreateDelegate([Action[object]], $selectLanguage)
    $null = Invoke-Store (New-Store $root) Load @($unexpected, $startupLanguage)
    $startupForm = [Activator]::CreateInstance($assembly.GetType('TorrentChecker.frmAppOptions', $true))
    try {
        Assert ($startupForm.Controls.Find('btnSave', $true)[0].Text -ceq $saveLabels['kk']) 'Startup settings localize the first form'
    } finally {
        $startupForm.Dispose()
        $selectLanguage.Invoke($null, @('ru'))
    }
    Assert ($reloaded['AppOptions']['language'] -ceq 'kk') 'Selected language survives save and reload'
    Assert ($reloaded['proxyCredential'].Password -ceq $proxySecret -and
            $reloaded['proxyCredential'].UserName -ceq $user) 'Restore proxy credentials from Windows vault'
    Assert ($reloaded['KeyWordsParams'][7]['client_command_template'] -ceq '' -and
            $reloaded['KeyWordsParams'][2]['client_command_template'] -ceq '%ClientPath% %TorrentSource%') 'Reload explicitly cleared mixed command and new RuTracker command'

    $reloaded['dtFoundTorrents'].Rows[0]['topic_name'] = 'Updated test title'
    $null = Invoke-Store $store Save @($reloaded, $false)
    $rawJson = [IO.File]::ReadAllText($settingsPath)
    $ordinarySave = $rawJson | ConvertFrom-Json
    $reloaded = Invoke-Store (New-Store $root) Load @($unexpected)
    Assert ($ordinarySave.credentialId -eq $manifest.credentialId -and $ordinarySave.historyId -ne $manifest.historyId -and
            $reloaded['dtFoundTorrents'].Rows[0]['topic_name'] -eq 'Updated test title' -and
            $reloaded['proxyCredential'].Password -ceq $proxySecret) 'Ordinary history save retains existing vault entries'
    $manifest = $ordinarySave
    $historyPath = Join-Path $root ('history-' + $manifest.historyId + '.json')

    $otherEndpoint = $rawJson.Replace('"3128"', '"3129"')
    [IO.File]::WriteAllText($settingsPath, $otherEndpoint)
    $changed = Invoke-Store (New-Store $root) Load @($unexpected)
    Assert ($changed['proxyCredential'].Password -eq '') 'Editing proxy endpoint in JSON cannot reuse the saved password'
    [IO.File]::WriteAllText($settingsPath, $rawJson.Replace($manifest.historyId, '../outside'))
    $broken = $false
    try { $null = Invoke-Store (New-Store $root) Load @($unexpected) }
    catch { $broken = $_.Exception.InnerException -is [IO.InvalidDataException] }
    Assert ($broken -and (Test-Path -LiteralPath $historyPath)) 'Reject invalid history ID without retrying legacy or modifying history'

    $savedRetention = [TorrentChecker.mdlMain]::TORRENTS_MAX_STORETIME
    try {
        [TorrentChecker.mdlMain]::TORRENTS_MAX_STORETIME = 42
        [IO.File]::WriteAllText($settingsPath, $rawJson)
        foreach ($retention in @([long]0, [long]-1, [long]1, [long]20000000001, [long]::MaxValue)) {
            $wire = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            $wire.historyRetention = $retention
            [IO.File]::WriteAllText($settingsPath, ($wire | ConvertTo-Json -Depth 30))
            $retentionStore = New-Store $root
            $loaded = Invoke-Store $retentionStore Load @($unexpected)
            $expected = if ($retention -le 0) { [long]2592000 } else { $retention }
            Assert ($loaded['TORRENTS_MAX_STORETIME'] -eq $expected) "JSON retention $retention loads exactly or recovers to fixed default"
            $null = Invoke-Store $retentionStore Save @($loaded, $false)
            $manifest = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            Assert ($manifest.historyRetention -eq $expected) "Next save persists retention $expected"
            $loaded = Invoke-Store (New-Store $root) Load @($unexpected)
            Assert ($loaded['TORRENTS_MAX_STORETIME'] -eq $expected) "Saved retention $expected reloads exactly"
        }
        foreach ($retention in @([long]0, [long]-1)) {
            $importRoot = Join-Path $root ('retention-' + [Guid]::NewGuid().ToString('N'))
            $null = [IO.Directory]::CreateDirectory($importRoot)
            $importStore = New-Store $importRoot
            $importPath = Join-Path $importRoot 'settings.dat'
            $importManifest = $null
            try {
                $fixture['TORRENTS_MAX_STORETIME'] = $retention
                $stream = [IO.File]::Create($importPath)
                try {
                    $gzip = New-Object IO.Compression.GZipStream($stream, [IO.Compression.CompressionMode]::Compress)
                    try { (New-Object Runtime.Serialization.Formatters.Binary.BinaryFormatter).Serialize($gzip, $fixture) }
                    finally { $gzip.Dispose() }
                } finally { $stream.Dispose() }
                $sourceHash = (Get-FileHash -LiteralPath $importPath).Hash
                $loaded = Invoke-Store $importStore Load @($accept)
                $importManifest = Get-Content -LiteralPath (Join-Path $importRoot 'settings.json') -Raw | ConvertFrom-Json
                Assert ($loaded['TORRENTS_MAX_STORETIME'] -eq 2592000 -and $loaded['KeyWordsParams'][7]['label'] -ceq $rules[7]['label']) "Legacy retention $retention recovers and retains usable rules"
                $null = Invoke-Store $importStore Save @($loaded, $false)
                $importManifest = Get-Content -LiteralPath (Join-Path $importRoot 'settings.json') -Raw | ConvertFrom-Json
                Assert ($importManifest.historyRetention -eq 2592000) 'Next legacy save persists recovered retention'
                Assert ((Get-FileHash -LiteralPath $importPath).Hash -eq $sourceHash) 'Retention recovery preserves legacy source bytes'
            } finally {
                if ($null -ne $importManifest) {
                    foreach ($service in @('rutracker', 'kinozal', 'proxy')) {
                        $target = Invoke-Store $importStore Target @($importManifest.credentialId, $service)
                        $null = $vaultType.GetMethod('DeleteCredential', $staticFlags).Invoke($null, @($target))
                    }
                }
            }
        }
    } finally { [TorrentChecker.mdlMain]::TORRENTS_MAX_STORETIME = $savedRetention }
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
    try {
        if ($null -ne $manifest) {
            $services = @('rutracker', 'kinozal', 'proxy')
            foreach ($id in @($manifest.credentialId, $initialManifest.credentialId) | Select-Object -Unique) {
                foreach ($service in $services) {
                    $target = Invoke-Store $store Target @($id, $service)
                    $null = $vaultType.GetMethod('DeleteCredential', $staticFlags).Invoke($null, @($target))
                }
            }
        }
    } finally {
        [IO.Directory]::Delete($root, $true)
    }
}
$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
try {
    $compilerOptions = New-Object System.CodeDom.Compiler.CompilerParameters
    $compilerOptions.GenerateInMemory = $true
    $compilerOptions.ReferencedAssemblies.AddRange([string[]]@($assembly.Location, 'System.dll', 'System.Net.Http.dll', 'System.Core.dll', 'System.Windows.Forms.dll', 'System.Data.dll', 'System.Drawing.dll', 'System.Xml.dll'))
    $compiledTests = $provider.CompileAssemblyFromFile($compilerOptions, ([string[]]@((Join-Path $PSScriptRoot 'HttpTests.cs'), (Join-Path $PSScriptRoot 'ScanTests.cs'), (Join-Path $PSScriptRoot 'OptionsTests.cs'), (Join-Path $PSScriptRoot 'TorrentNameTests.cs'))))
    if ($compiledTests.Errors.HasErrors) { throw ($compiledTests.Errors | Out-String) }
} finally { $provider.Dispose() }
$script:checks += [HttpTests]::Run()
$script:checks += [ScanTests]::Run()
$script:checks += [OptionsTests]::Run()
$script:checks += [TorrentNameTests]::Run()
Write-Host "PASS: $script:checks checks ($Configuration)"
