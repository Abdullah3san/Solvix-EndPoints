/**
 * Generates an initial password for a new company owner. Uses the browser's
 * cryptographic RNG and rejection sampling (no modulo bias). The result always
 * satisfies the API's policy: at least 10 characters with a letter and a digit.
 */
const LETTERS = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz"; // no look-alikes (I, l, O, o)
const DIGITS = "23456789";
const ALPHABET = LETTERS + DIGITS;

function pick(alphabet: string): string {
  const limit = 256 - (256 % alphabet.length);
  const byte = new Uint8Array(1);
  do {
    crypto.getRandomValues(byte);
  } while (byte[0] >= limit);
  return alphabet[byte[0] % alphabet.length];
}

export function generatePassword(length = 16): string {
  const size = Math.max(10, length);
  const chars = [pick(LETTERS), pick(DIGITS)];
  while (chars.length < size) chars.push(pick(ALPHABET));
  // Fisher–Yates so the guaranteed characters are not always first.
  for (let i = chars.length - 1; i > 0; i--) {
    const j = Number(pickIndex(i + 1));
    [chars[i], chars[j]] = [chars[j], chars[i]];
  }
  return chars.join("");
}

function pickIndex(n: number): number {
  const limit = 256 - (256 % n);
  const byte = new Uint8Array(1);
  do {
    crypto.getRandomValues(byte);
  } while (byte[0] >= limit);
  return byte[0] % n;
}

export function meetsPasswordPolicy(password: string): boolean {
  return password.length >= 10 && password.length <= 256 && /[a-zA-Z]/.test(password) && /\d/.test(password);
}
