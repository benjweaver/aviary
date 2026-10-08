# Updates Aviary installed by install.ps1 to the latest release. Aviary never
# goes online by itself, so this is how it gets new versions.
#
#   irm https://raw.githubusercontent.com/benjweaver/aviary/main/packaging/windows/update.ps1 | iex
#
# The update itself lives in install.ps1, next to the install it repeats.
& ([scriptblock]::Create((Invoke-RestMethod 'https://raw.githubusercontent.com/benjweaver/aviary/main/packaging/windows/install.ps1' -UseBasicParsing))) -Update
