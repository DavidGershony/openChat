# Android Release Signing

## Overview

The Android APK is signed with a release keystore during CI builds. The keystore is stored as a GitHub secret (never committed to the repo).

**IMPORTANT:** If you lose the keystore, existing users cannot update the app — they must uninstall and reinstall. Back up the keystore file securely.

## Keystore Details

- **File:** `src/OpenChat.Android/openchat-release.keystore` (gitignored)
- **Alias:** `openchat`
- **Algorithm:** RSA 2048-bit
- **Validity:** 10,000 days (~27 years)
- **Store/Key password:** `openchat-release` (change for production)

## GitHub Secrets Setup

Go to your repo **Settings > Secrets and variables > Actions > New repository secret** and add these 4 secrets:

### 1. `ANDROID_KEYSTORE_BASE64`

Base64-encoded keystore file. Generate with:

```bash
base64 -w 0 src/OpenChat.Android/openchat-release.keystore
```

Paste the full output as the secret value.

### 2. `ANDROID_KEY_ALIAS`

```
openchat
```

### 3. `ANDROID_KEY_PASSWORD`

```
openchat-release
```

### 4. `ANDROID_KEYSTORE_PASSWORD`

```
openchat-release
```

## How It Works

The publish workflow (`.github/workflows/publish.yml`):

1. Decodes `ANDROID_KEYSTORE_BASE64` back into a `.keystore` file
2. Passes signing properties to `dotnet publish` via MSBuild args
3. The csproj enables `AndroidKeyStore=true` when `AndroidSigningKeyStore` is set in Release mode

## Local Signing (Optional)

To build a signed APK locally:

```bash
dotnet publish src/OpenChat.Android/OpenChat.Android.csproj \
  --configuration Release \
  --framework net9.0-android \
  -p:AndroidSigningKeyStore=openchat-release.keystore \
  -p:AndroidSigningKeyAlias=openchat \
  -p:AndroidSigningKeyPass=openchat-release \
  -p:AndroidSigningStorePass=openchat-release \
  --output ./publish/android
```

## Regenerating the Keystore

If you need a new keystore (breaks updates for existing users):

```bash
keytool -genkeypair -v \
  -keystore src/OpenChat.Android/openchat-release.keystore \
  -alias openchat \
  -keyalg RSA -keysize 2048 -validity 10000 \
  -storepass <password> -keypass <password> \
  -dname "CN=OpenChat, OU=Development, O=OpenChat, L=Unknown, ST=Unknown, C=US"
```

Then update all 4 GitHub secrets with the new values.
