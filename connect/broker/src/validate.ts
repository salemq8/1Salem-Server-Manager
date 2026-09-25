// Formats of every identifier and field the broker accepts. Path patterns reuse these, so a
// malformed id never reaches the database and simply falls into the generic 404.

import type { Env } from "./env";

export const SERVER_ID = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
export const MEMBERSHIP_ID = "mem_[a-z2-7]{26}";
export const INVITE_ID = "inv_[a-z2-7]{26}";
export const DEVICE_ID = "dev_[a-z2-7]{26}";
/** 128-bit jti, base64url. */
export const JTI = "[A-Za-z0-9_-]{22}";

const SERVER_ID_EXACT = new RegExp(`^${SERVER_ID}$`);
const MEMBERSHIP_ID_EXACT = new RegExp(`^${MEMBERSHIP_ID}$`);
export const isServerId = (text: string) => SERVER_ID_EXACT.test(text);
export const isMembershipId = (text: string) => MEMBERSHIP_ID_EXACT.test(text);

// Tailscale StableNodeIDs are short ASCII tokens (for example "n1a2b3CNTRL"); fake mode uses
// readable ids. Both fit this conservative shape.
const NODE_ID = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/;
export const isNodeId = (text: string) => NODE_ID.test(text);

// Opaque enrollment ciphertext: printable ASCII (base64, base64url or dotted compact forms),
// bounded well inside the 16 KiB body limit.
const CIPHERTEXT = /^[\x21-\x7e]{1,12288}$/;
export const isCiphertext = (text: string) => CIPHERTEXT.test(text);

// Labels are shown to friends. Control characters and bidi overrides/isolates are refused because
// they can make one server's name render as another's.
const LABEL_FORBIDDEN = /[\p{Cc}‪-‮⁦-⁩]/u;
export function isLabel(text: string): boolean {
  const length = [...text].length;
  return length >= 1 && length <= 64 && text.trim() === text && !LABEL_FORBIDDEN.test(text);
}

function parsePort(text: string): number | null {
  if (!/^[1-9][0-9]{0,4}$/.test(text)) return null;
  const port = Number(text);
  return port <= 65535 ? port : null;
}

/** Strict dotted-quad IPv4 (no leading zeros) to four octets. */
export function parseIPv4(text: string): number[] | null {
  const parts = text.split(".");
  if (parts.length !== 4) return null;
  const octets: number[] = [];
  for (const part of parts) {
    if (!/^(0|[1-9][0-9]{0,2})$/.test(part)) return null;
    const value = Number(part);
    if (value > 255) return null;
    octets.push(value);
  }
  return octets;
}

/** Expands a lowercase IPv6 literal (no zone, no embedded IPv4) to eight 16-bit groups. */
export function parseIPv6(text: string): number[] | null {
  if (!/^[0-9a-f:]+$/.test(text)) return null;
  const halves = text.split("::");
  if (halves.length > 2) return null;
  const toGroups = (part: string) => (part === "" ? [] : part.split(":"));
  const head = toGroups(halves[0]!);
  const tail = halves.length === 2 ? toGroups(halves[1]!) : [];
  const missing = 8 - head.length - tail.length;
  if (halves.length === 2 ? missing < 1 : missing !== 0) return null;
  const groups = [...head, ...Array<string>(halves.length === 2 ? missing : 0).fill("0"), ...tail];
  const values: number[] = [];
  for (const group of groups) {
    if (!/^[0-9a-f]{1,4}$/.test(group)) return null;
    values.push(parseInt(group, 16));
  }
  return values;
}

/** Loopback bridges exist only for the fake transport mode on a local development broker. */
export const loopbackBridgeAllowed = (env: Env) => env.CONNECT_DEV_LOOPBACK_BRIDGE === "allow";

/**
 * A host bridge address is `ip:port` (or `[ipv6]:port`) inside the Tailscale ranges
 * 100.64.0.0/10 and fd7a:115c:a1e0::/48, or, only when `allowLoopback` is set, a loopback
 * address for fake mode. It becomes the ticket's `hb` claim, the only address a friend
 * transport will ever dial.
 */
export function isHostBridge(text: string, allowLoopback: boolean): boolean {
  const v6 = /^\[([^\]]+)\]:([0-9]+)$/.exec(text);
  if (v6 !== null) {
    const groups = parseIPv6(v6[1]!);
    if (groups === null || parsePort(v6[2]!) === null) return false;
    const tailscale = groups[0] === 0xfd7a && groups[1] === 0x115c && groups[2] === 0xa1e0;
    const loopback = groups.slice(0, 7).every((g) => g === 0) && groups[7] === 1;
    return tailscale || (allowLoopback && loopback);
  }
  const v4 = /^([0-9.]+):([0-9]+)$/.exec(text);
  if (v4 === null) return false;
  const octets = parseIPv4(v4[1]!);
  if (octets === null || parsePort(v4[2]!) === null) return false;
  const tailscale = octets[0] === 100 && octets[1]! >= 64 && octets[1]! <= 127;
  const loopback = octets[0] === 127;
  return tailscale || (allowLoopback && loopback);
}
