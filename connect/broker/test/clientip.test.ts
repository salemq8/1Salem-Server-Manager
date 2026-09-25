import { describe, expect, it } from "vitest";
import { clientNetwork } from "../src/clientip";

describe("client network an IP rate-limit bucket counts", () => {
  it("keeps IPv4 addresses exact", () => {
    expect(clientNetwork("203.0.113.7")).toBe("203.0.113.7");
    expect(clientNetwork("203.0.113.8")).not.toBe(clientNetwork("203.0.113.7"));
  });

  it("cuts IPv6 to its /64 whatever the interface id or spelling", () => {
    const network = clientNetwork("2001:db8:1:2::1");
    expect(network).toBe("2001:db8:1:2::/64");
    for (const address of [
      "2001:db8:1:2:ffff:ffff:ffff:ffff",
      "2001:0DB8:0001:0002:0:0:0:9",
      "2001:db8:1:2:a:b:c:d",
      "2001:db8:1:2::",
    ]) {
      expect(clientNetwork(address), address).toBe(network);
    }
    expect(clientNetwork("2001:db8:1:3::1")).not.toBe(network);
    expect(clientNetwork("2001:db8:1::1")).toBe("2001:db8:1:0::/64");
  });

  it("counts an IPv4-mapped IPv6 address as the IPv4 client it carries", () => {
    for (const address of ["::ffff:198.51.100.9", "::FFFF:198.51.100.9", "::ffff:c633:6409", "0:0:0:0:0:ffff:c633:6409"]) {
      expect(clientNetwork(address), address).toBe("198.51.100.9");
    }
  });

  it("puts every unparseable value, including a missing header, in one shared bucket", () => {
    for (const address of [
      "",
      "unknown",
      "198.51.100",
      "198.51.100.256",
      "198.051.100.9",
      " 198.51.100.9",
      "198.51.100.9:443",
      "1.2.3.4.5",
      "[2001:db8::1]",
      "fe80::1%eth0",
      "2001:db8::1::2",
      "2001:db8:1:2:3:4:5:6:7",
      "2001:db8:12345::1",
      "::ffff:198.51.100.256",
      "g::1",
    ]) {
      expect(clientNetwork(address), address).toBe("unknown");
    }
  });
});
