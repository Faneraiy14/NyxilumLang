#!/usr/bin/env bash
# build-apk.sh — зібрати Android-застосунок (APK) з програми на NyxilumLang.
# Без Android Studio і без Gradle: кожен крок - окрема зрозуміла команда.
#
#   android/build-apk.sh app.nx --package com.example.app --label "Назва" \
#       [--scheme app] [--version 1] [--headless] [-o app.apk]
#
# --headless: без інтерфейсу - екран прозорий, застосунок робить свою
# справу (напр. ставить будильники) і закривається сам (finishScreen()).
# Код інтерфейсу (uiText/uiButton...) при цьому нікуди не дівається.
#
# Потрібно: nx, JDK (javac, keytool), zip, aarch64-linux-gnu-as/ld,
# Android SDK ($ANDROID_HOME, типово ~/Android/Sdk) з platforms;android-36
# і build-tools (aapt2, d8, zipalign, apksigner).
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
NX="${NX:-nx}"
SDK="${ANDROID_HOME:-$HOME/Android/Sdk}"

src="" pkg="" label="" scheme="" version=1 out="" theme="@android:style/Theme.DeviceDefault.NoActionBar"
while [ $# -gt 0 ]; do
    case "$1" in
        --package) pkg="$2"; shift 2 ;;
        --label) label="$2"; shift 2 ;;
        --scheme) scheme="$2"; shift 2 ;;
        --version) version="$2"; shift 2 ;;
        --headless) theme="@android:style/Theme.Translucent.NoTitleBar"; shift ;;
        -o) out="$2"; shift 2 ;;
        *) src="$1"; shift ;;
    esac
done
if [ -z "$src" ] || [ -z "$pkg" ] || [ -z "$label" ]; then
    echo "Використання: $0 app.nx --package com.example.app --label \"Назва\" [--scheme app] [--version N] [-o app.apk]" >&2
    exit 1
fi
[ -z "$scheme" ] && scheme="${pkg##*.}"
[ -z "$out" ] && out="$(basename "${src%.nx}").apk"

platform=$(ls -d "$SDK"/platforms/android-* 2>/dev/null | sort -V | tail -1)
tools=$(ls -d "$SDK"/build-tools/* 2>/dev/null | sort -V | tail -1)
if [ -z "$platform" ] || [ -z "$tools" ]; then
    echo "Немає Android SDK у $SDK (потрібні platforms;android-36 і build-tools)." >&2
    exit 1
fi
jar="$platform/android.jar"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/classes" "$work/apk/lib/arm64-v8a"

echo "1/6 NyxilumLang -> ARM64 (lib/arm64-v8a/libnxapp.so)"
"$NX" compile-native "$src" -o "$work/apk/lib/arm64-v8a/libnxapp.so" --target android-arm64 > "$work/nx.log" \
    || { cat "$work/nx.log" >&2; exit 1; }
rm -f "$work/apk/lib/arm64-v8a/libnxapp.so.s" "$work/apk/lib/arm64-v8a/libnxapp.so.o"

echo "2/6 перекладач: Java -> .class"
javac -nowarn -Xlint:-options -source 8 -target 8 -bootclasspath "$jar:$tools/core-lambda-stubs.jar" -encoding UTF-8 \
    -d "$work/classes" "$HERE"/bridge/src/nyx/bridge/*.java

echo "3/6 .class -> classes.dex (d8)"
"$tools/d8" --release --min-api 26 --lib "$jar" --output "$work/apk" $(find "$work/classes" -name '*.class')

echo "4/6 маніфест -> APK (aapt2)"
sed -e "s|@PACKAGE@|$pkg|" -e "s|@LABEL@|$label|" -e "s|@SCHEME@|$scheme|" \
    -e "s|@VERSION_CODE@|$version|" -e "s|@VERSION_NAME@|$version|" -e "s|@THEME@|$theme|" \
    "$HERE/AndroidManifest.xml" > "$work/AndroidManifest.xml"
"$tools/aapt2" link -o "$work/base.apk" -I "$jar" --manifest "$work/AndroidManifest.xml" \
    --min-sdk-version 26 --target-sdk-version 36
(cd "$work/apk" && zip -q -r "$work/base.apk" classes.dex lib)

echo "5/6 вирівнювання (zipalign)"
"$tools/zipalign" -f -p 4 "$work/base.apk" "$work/aligned.apk"

echo "6/6 підпис (apksigner, власний debug-ключ)"
# Ключ для тестових збірок; створюється один раз. Пароль "android" - той
# самий стандарт, що й у debug-ключа Android Studio (для публікації в
# магазині потрібен окремий справжній ключ).
ks="$HOME/.android/nyx-debug.keystore"
if [ ! -f "$ks" ]; then
    mkdir -p "$(dirname "$ks")"
    keytool -genkeypair -keystore "$ks" -storepass android -keypass android -alias nyxdebug \
        -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=NyxilumLang Debug" > /dev/null 2>&1
fi
"$tools/apksigner" sign --ks "$ks" --ks-pass pass:android --ks-key-alias nyxdebug --out "$out" "$work/aligned.apk"

echo "✅ $out ($(du -h "$out" | cut -f1)) - встановити: adb install -r $out"
