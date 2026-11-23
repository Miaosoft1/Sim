export function isPlainObject(value) {
  if (value === null || typeof value !== "object") return false;
  const prototype = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}

export function clone(value) {
  if (Array.isArray(value)) return value.map(clone);
  if (isPlainObject(value)) {
    const copy = {};
    for (const [key, child] of Object.entries(value)) copy[key] = clone(child);
    return copy;
  }
  return value;
}

export function deepFreeze(value) {
  if (value === null || typeof value !== "object" || Object.isFrozen(value)) return value;
  for (const child of Object.values(value)) deepFreeze(child);
  return Object.freeze(value);
}

export function finiteNumber(value) {
  return typeof value === "number" && Number.isFinite(value);
}

export function positiveInteger(value) {
  return Number.isInteger(value) && value >= 1;
}

export function guid(value) {
  return typeof value === "string" &&
    /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i.test(value);
}

export function assertObject(value, message) {
  if (!isPlainObject(value)) throw new TypeError(message);
  return value;
}

export function assertKnownKeys(value, allowed, context) {
  for (const key of Object.keys(value)) {
    if (!allowed.includes(key)) throw new TypeError(`Unknown field '${key}' in ${context}.`);
  }
}

export function normalizeGuid(value) {
  if (!guid(value)) throw new TypeError(`Invalid GUID '${value}'.`);
  if (value.includes("-")) return value.toLowerCase();
  return `${value.slice(0, 8)}-${value.slice(8, 12)}-${value.slice(12, 16)}-${value.slice(16, 20)}-${value.slice(20)}`.toLower();
}

export function randomUuid() {
  if (typeof globalThis.crypto?.randomUUID === "function") return globalThis.crypto.randomUUID();
  if (typeof globalThis.crypto?.getRandomValues === "function") {
    const bytes = new Uint8Array(16);
    globalThis.crypto.getRandomValues(bytes);
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
    return normalizeGuid(hex);
  }
  const hex = () => Math.floor(Math.random() * 0xffffffff).toString(16).padStart(8, "0");
  return `${hex().slice(0, 8)}-${hex().slice(8, 12)}-${hex().slice(0, 4)}-${hex().slice(4, 8)}-${hex()}${hex().slice(0, 4)}`;
}
