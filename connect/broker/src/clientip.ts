// The client network an IP-keyed rate-limit bucket counts (§14).
//
// One IPv6 subscriber is normally delegated at least a whole /64, so counting exact IPv6 addresses
// would hand anyone 2^64 fresh buckets: IPv6 is cut to its /64. An IPv4-mapped address
// (::ffff:a.b.c.d) is the IPv4 client it carries, so both spellings share one bucket. Anything
// unparseable, including a missing header (local dev), shares a single "unknown" bucket instead of
// getting one of its own.

import { parseIPv4, parseIPv6 } from "./validate";

export const UNKNOWN_NETWORK = "unknown";

/** "a.b.c.d" for IPv4 (and IPv4-mapped IPv6), "g0:g1:g2:g3::/64" for IPv6, else "unknown". */
export function clientNetwork(address: string): string {
  const v4 = parseIPv4(address);
  if (v4 !== null) return v4.join(".");
  const v6 = parseIPv6WithDottedTail(address.toLowerCase());
  if (v6 === null) return UNKNOWN_NETWORK;
  const [g0, g1, g2, g3, g4, g5, g6, g7] = v6 as [number, number, number, number, number, number, number, number];
  if (g0 === 0 && g1 === 0 && g2 === 0 && g3 === 0 && g4 === 0 && g5 === 0xffff) {
    return [g6 >> 8, g6 & 0xff, g7 >> 8, g7 & 0xff].join(".");
  }
  return `${[g0, g1, g2, g3].map((g) => g.toString(16)).join(":")}::/64`;
}

/** The shared IPv6 parser takes hex groups only, so a dotted IPv4 tail becomes two groups first. */
function parseIPv6WithDottedTail(text: string): number[] | null {
  const colon = text.lastIndexOf(":");
  const tail = text.slice(colon + 1);
  if (!tail.includes(".")) return parseIPv6(text);
  const v4 = colon < 0 ? null : parseIPv4(tail);
  if (v4 === null) return null;
  const [a, b, c, d] = v4 as [number, number, number, number];
  return parseIPv6(`${text.slice(0, colon + 1)}${((a << 8) | b).toString(16)}:${((c << 8) | d).toString(16)}`);
}
