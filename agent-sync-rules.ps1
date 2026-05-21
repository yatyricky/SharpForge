# copy .github/**/*.md to .clinerules/*.md, flattening the directory structure
Get-ChildItem -Path .github -Recurse -Filter *.md | ForEach-Object {
    $destinationPath = Join-Path -Path .clinerules -ChildPath $_.Name
    Copy-Item -Path $_.FullName -Destination $destinationPath -Force
}