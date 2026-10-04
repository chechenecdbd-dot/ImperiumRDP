$lines = @(
    'ЛИЦЕНЗИОННОЕ СОГЛАШЕНИЕ IMPERIUMRDP',
    '',
    '1. Настоящее программное обеспечение (ImperiumRDP) предоставляется «как есть», без каких-либо гарантий. Автор не несёт ответственности за любой ущерб, связанный с использованием программы.',
    '',
    '2. Разрешается свободное использование, копирование и изменение программы для внутренних нужд организации-владельца компьютерного клуба, включая установку на все машины клуба и администраторские рабочие места.',
    '',
    '3. Запрещается распространение программы третьим лицам в коммерческих целях, а также использование для несанкционированного доступа к чужим компьютерам. Программа предназначена только для администрирования собственных машин.',
    '',
    '4. Устанавливая программу, вы подтверждаете, что являетесь владельцем или уполномоченным администратором компьютеров, на которые она устанавливается.',
    '',
    '5. Все действия в удалённой сессии выполняются от имени пользователя клубного компьютера и логируются локально (agent.log).'
)

$sb = New-Object System.Text.StringBuilder
[void]$sb.Append('{\rtf1\ansi\ansicpg1251\deff0{\fonttbl{\f0\fnil\fcharset204 Segoe UI;}}')
[void]$sb.Append('\fs20')
foreach ($line in $lines) {
    [void]$sb.Append('\par ')
    foreach ($ch in $line.ToCharArray()) {
        $code = [int]$ch
        if ($code -gt 127) { [void]$sb.Append('\u' + $code + '?') }
        elseif ($ch -eq '\') { [void]$sb.Append('\\\\') }
        elseif ($ch -eq '{') { [void]$sb.Append('\{') }
        elseif ($ch -eq '}') { [void]$sb.Append('\}') }
        else { [void]$sb.Append($ch) }
    }
}
[void]$sb.Append('}')
[IO.File]::WriteAllText('D:\ImperiumRDP\installer\license.rtf', $sb.ToString(), [System.Text.Encoding]::ASCII)
Write-Host ("license.rtf: " + (Get-Item 'D:\ImperiumRDP\installer\license.rtf').Length + " байт")
