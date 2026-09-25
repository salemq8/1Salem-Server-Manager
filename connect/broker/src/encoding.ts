// Byte encodings used on the wire. Decoders are strict: they return null for anything that is not
// the single canonical encoding of some byte string (no padding, no whitespace, no stray low
// bits), so one value can never be presented in two textual forms.

const B64URL = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
const B64STD = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
const B32 = "abcdefghijklmnopqrstuvwxyz234567";

const utf8Encoder = new TextEncoder();
const utf8Decoder = new TextDecoder("utf-8", { fatal: true, ignoreBOM: false });

export function utf8(text: string): Uint8Array {
  return utf8Encoder.encode(text);
}

export function fromUtf8(bytes: Uint8Array): string | null {
  try {
    return utf8Decoder.decode(bytes);
  } catch {
    return null;
  }
}

function encode64(bytes: Uint8Array, alphabet: string, pad: boolean): string {
  let out = "";
  let i = 0;
  for (; i + 2 < bytes.length; i += 3) {
    const n = (bytes[i]! << 16) | (bytes[i + 1]! << 8) | bytes[i + 2]!;
    out += alphabet[n >> 18]! + alphabet[(n >> 12) & 63]! + alphabet[(n >> 6) & 63]! + alphabet[n & 63]!;
  }
  const rest = bytes.length - i;
  if (rest === 1) {
    const n = bytes[i]! << 16;
    out += alphabet[n >> 18]! + alphabet[(n >> 12) & 63]! + (pad ? "==" : "");
  } else if (rest === 2) {
    const n = (bytes[i]! << 16) | (bytes[i + 1]! << 8);
    out += alphabet[n >> 18]! + alphabet[(n >> 12) & 63]! + alphabet[(n >> 6) & 63]! + (pad ? "=" : "");
  }
  return out;
}

function decode64(text: string, alphabet: string): Uint8Array | null {
  if (text.length % 4 === 1) return null;
  const out = new Uint8Array(Math.floor((text.length * 3) / 4));
  let bits = 0;
  let value = 0;
  let o = 0;
  for (let i = 0; i < text.length; i++) {
    const v = alphabet.indexOf(text[i]!);
    if (v < 0) return null;
    value = (value << 6) | v;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      out[o++] = (value >> bits) & 0xff;
    }
    value &= (1 << bits) - 1;
  }
  // Leftover bits must be zero, otherwise several strings would decode to the same bytes.
  if ((value & ((1 << bits) - 1)) !== 0) return null;
  return out;
}

export function base64UrlEncode(bytes: Uint8Array): string {
  return encode64(bytes, B64URL, false);
}

export function base64UrlDecode(text: string): Uint8Array | null {
  return decode64(text, B64URL);
}

/** Standard base64 with padding (RFC 4648 §4), as used for the PKCS#8 secret. Whitespace is ignored. */
export function base64StdDecode(text: string): Uint8Array | null {
  const compact = text.replace(/\s+/g, "");
  if (compact.length % 4 !== 0) return null;
  const unpadded = compact.replace(/={1,2}$/, "");
  const bytes = decode64(unpadded, B64STD);
  if (bytes === null || encode64(bytes, B64STD, true) !== compact) return null;
  return bytes;
}

/** RFC 4648 base32, lowercase alphabet, no padding. */
export function base32Lower(bytes: Uint8Array): string {
  let out = "";
  let bits = 0;
  let value = 0;
  for (const byte of bytes) {
    value = (value << 8) | byte;
    bits += 8;
    while (bits >= 5) {
      bits -= 5;
      out += B32[(value >> bits) & 31]!;
    }
    value &= (1 << bits) - 1;
  }
  if (bits > 0) out += B32[(value << (5 - bits)) & 31]!;
  return out;
}

export function hex(bytes: Uint8Array): string {
  let out = "";
  for (const byte of bytes) out += byte.toString(16).padStart(2, "0");
  return out;
}
