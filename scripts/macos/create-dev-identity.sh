#!/usr/bin/env bash
# create-dev-identity.sh — Create the stable local code-signing identity that
# keeps TCC grants alive across rebuilds.
#
# Why this exists
# ---------------
# Ad-hoc signing (codesign --sign "-") gives a bundle a designated requirement
# made of its own cdhash, so *every rebuild is a different app to TCC*. Screen
# Recording and Accessibility silently stop applying — the System Settings
# toggle still shows as on while the API returns false — and re-granting needs
# the user's password every single deploy.
#
# Signing with a certificate instead makes the designated requirement
#   identifier "com.dave.lanmessenger" and certificate leaf = H"<cert hash>"
# which is identical for every build signed with the same certificate. Grant the
# app once, rebuild forever.
#
# This is a TCC-identity measure, not a distribution measure. The certificate is
# self-signed, so Gatekeeper trusts it no more than it trusts an ad-hoc bundle.
# Release builds are still signed with a real Developer ID by CI.
#
# Usage:
#   scripts/macos/create-dev-identity.sh            # create "LAN Messenger Dev"
#   DEV_SIGNING_IDENTITY="Other Name" scripts/macos/create-dev-identity.sh
#
# Afterwards, scripts/macos/package.sh picks the identity up automatically.
#
# Keychain Access does the same job through Certificate Assistant →
# "Create a Certificate…" → Name: LAN Messenger Dev, Identity Type: Self Signed
# Root, Certificate Type: Code Signing. Either route is fine; this one is
# scriptable and repeatable.
set -euo pipefail

IDENTITY_NAME="${DEV_SIGNING_IDENTITY:-LAN Messenger Dev}"
KEYCHAIN="${DEV_SIGNING_KEYCHAIN:-$HOME/Library/Keychains/login.keychain-db}"
VALID_DAYS="${DEV_SIGNING_DAYS:-3650}"

if [ "$(uname)" != "Darwin" ]; then
    echo "::error::create-dev-identity.sh must run on macOS (current uname: $(uname))"
    exit 1
fi

# ── Already there? ───────────────────────────────────────────────────────────
# Note the deliberate absence of -v. A self-signed root that was never added to
# the trust settings evaluates as CSSMERR_TP_NOT_TRUSTED, so `find-identity -v`
# ("valid identities only") does not list it — but codesign signs with it
# perfectly well, and trust is irrelevant to what we are after: the signature's
# designated requirement. package.sh detects the identity the same way.
if security find-identity -p codesigning "$KEYCHAIN" 2>/dev/null | grep -qF "\"$IDENTITY_NAME\""; then
    echo "▶  Identity \"$IDENTITY_NAME\" already exists in $KEYCHAIN — nothing to do."
    security find-identity -p codesigning "$KEYCHAIN" | grep -F "\"$IDENTITY_NAME\""
    exit 0
fi

command -v openssl >/dev/null 2>&1 || { echo "::error::openssl not found on PATH"; exit 1; }

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

echo "▶  Generating a self-signed code-signing certificate: $IDENTITY_NAME"

cat > "$WORK_DIR/openssl.cnf" <<EOF
[ req ]
distinguished_name = dn
x509_extensions    = v3
prompt             = no

[ dn ]
CN = $IDENTITY_NAME

[ v3 ]
basicConstraints     = critical,CA:false
keyUsage             = critical,digitalSignature
extendedKeyUsage     = critical,codeSigning
subjectKeyIdentifier = hash
EOF

openssl req -x509 -newkey rsa:2048 -nodes -days "$VALID_DAYS" \
    -config "$WORK_DIR/openssl.cnf" \
    -keyout "$WORK_DIR/key.pem" -out "$WORK_DIR/cert.pem" >/dev/null 2>&1

# The PKCS#12 must be written the old way. OpenSSL 3 defaults to an AES-256 /
# SHA-256 MAC that Security.framework cannot read, and `security import` fails
# with "MAC verification failed during PKCS12 import (wrong password?)" — which
# reads as a typo rather than as an algorithm mismatch. -legacy plus the three
# SHA1/3DES flags produce a bundle the keychain accepts.
P12_PASS="$(openssl rand -hex 16)"
openssl pkcs12 -export -legacy \
    -inkey "$WORK_DIR/key.pem" -in "$WORK_DIR/cert.pem" \
    -out "$WORK_DIR/identity.p12" -name "$IDENTITY_NAME" \
    -passout "pass:$P12_PASS" \
    -macalg sha1 -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES >/dev/null 2>&1

echo "▶  Importing into $KEYCHAIN"

# -A grants every application access to the private key. Without it the key
# carries an ACL that codesign trips over, and macOS puts up a "wants to sign
# using key" dialog on every build — which defeats the point of an unattended
# packaging script. This key signs nothing but local development builds.
security import "$WORK_DIR/identity.p12" \
    -k "$KEYCHAIN" \
    -P "$P12_PASS" \
    -T /usr/bin/codesign \
    -T /usr/bin/security \
    -A

echo ""
echo "▶  Done. package.sh will now prefer this identity for local builds:"
security find-identity -p codesigning "$KEYCHAIN" | grep -F "\"$IDENTITY_NAME\""
echo ""
echo "   Next: rebuild and reinstall once, grant Screen Recording and"
echo "   Accessibility one final time, and the grants will survive every"
echo "   rebuild from here on."
echo ""
echo "   To undo: delete \"$IDENTITY_NAME\" from Keychain Access (login keychain,"
echo "   My Certificates), or:"
echo "     security delete-identity -c \"$IDENTITY_NAME\" \"$KEYCHAIN\""
