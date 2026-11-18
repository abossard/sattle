#!/bin/sh
# Install the offline narrator beside this film. Run from any directory.
set -eu
cd "$(dirname "$0")"
mkdir -p .narrator
cd .narrator
python3.12 -m venv venv
venv/bin/python -m pip install --disable-pip-version-check --no-cache-dir kokoro-onnx==0.5.0

release=https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.1
fetch() {
    file=$1
    hash=$2
    if [ -f "$file" ] && printf '%s  %s\n' "$hash" "$file" | shasum -a 256 -c --status; then
        return
    fi
    curl --fail --location --retry 2 --output "$file.part" "$release/$file"
    printf '%s  %s\n' "$hash" "$file.part" | shasum -a 256 -c
    mv "$file.part" "$file"
}
fetch kokoro-v1.0.onnx beb0d1848dee9a49da392cc3df26958d46cfa35d321edf434f52949153f0df3a
fetch voices-v1.0.bin bca610b8308e8d99f32e6fe4197e7ec01679264efed0cac9140fe9c29f1fbf7d
