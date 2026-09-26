#!/bin/bash
# CI diagnostic: the .NET iOS link step fails with
#   xcrun: sh -c 'xcodebuild -sdk .../MacOSX.sdk -find clang++' failed with exit code 16384
# Reproduce that lookup cold (no xcrun cache), from inside MSBuild, in a few working dirs.
set -x
echo "env bytes $(env | wc -c), vars $(env | wc -l)"
env | awk '{ if (length($0) > 1000) print substr($0,1,60) "... (" length($0) " chars)" }'
env | grep -E '^(SDKROOT|DEVELOPER_DIR|TMPDIR|HOME|XCODE|MSBUILD|DOTNET)' | cut -c1-200
SDK="$(xcrun --sdk macosx --show-sdk-path)"
for d in "$PWD" "$PWD/ios" "$PWD/ios/obj" /tmp; do
  mkdir -p "$d"; ( cd "$d" && xcodebuild -sdk "$SDK" -find clang++; echo "xcodebuild in $d exit=$?" )
done
xcrun --kill-cache
xcrun clang++ --version; echo "cold xcrun exit=$?"
xcodebuild -version; echo "xcodebuild -version exit=$?"
xcodebuild -showsdks | head -20
