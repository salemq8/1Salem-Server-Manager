// Session tickets (§9): compact JWS, ES256 with a P1363 signature, fixed header layout.

import { ES256, ES256_SIGNATURE_BYTES } from "./crypto";
import { base64UrlEncode, utf8 } from "./encoding";
import type { TicketSigner } from "./secrets";

export const TICKET_ISSUER = "1salem-connect-broker";
export const TICKET_TYPE = "1salem-ticket+jwt";
/** Hosts accept exp - iat <= 900; the broker always issues 600. */
export const TICKET_LIFETIME_SECONDS = 600;

export interface TicketClaims {
  iss: typeof TICKET_ISSUER;
  aud: string;
  jti: string;
  sub: string;
  mid: string;
  sid: string;
  proto: "tcp";
  nid: string;
  skp: string;
  hb: string;
  av: number;
  iat: number;
  nbf: number;
  exp: number;
}

export async function signTicket(signer: TicketSigner, claims: TicketClaims): Promise<string> {
  // Key order is fixed so the encoded header is byte-identical for every ticket of one key.
  const header = { alg: "ES256", typ: TICKET_TYPE, kid: signer.kid };
  const payload: TicketClaims = {
    iss: claims.iss,
    aud: claims.aud,
    jti: claims.jti,
    sub: claims.sub,
    mid: claims.mid,
    sid: claims.sid,
    proto: claims.proto,
    nid: claims.nid,
    skp: claims.skp,
    hb: claims.hb,
    av: claims.av,
    iat: claims.iat,
    nbf: claims.nbf,
    exp: claims.exp,
  };
  const signingInput =
    base64UrlEncode(utf8(JSON.stringify(header))) + "." + base64UrlEncode(utf8(JSON.stringify(payload)));
  // Web Crypto ECDSA signatures are already IEEE P1363 (r||s); the length check guards the contract.
  const signature = new Uint8Array(await crypto.subtle.sign(ES256, signer.privateKey, utf8(signingInput)));
  if (signature.byteLength !== ES256_SIGNATURE_BYTES) throw new Error("unexpected ES256 signature length");
  return `${signingInput}.${base64UrlEncode(signature)}`;
}
