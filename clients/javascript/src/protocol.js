import yaml from "js-yaml";
import { ObservationError, ProtocolError } from "./errors.js";
import { assertKnownKeys, assertObject, clone, guid, normalizeGuid } from "./utils.js";

export const SESSION_PROTOCOL_VERSION = 5;
export const OPERATION_RECEIPT_WINDOW = 1024;
export const EVENT_ENVELOPE_VERSION = 2;

export const EventDirection = Object.freeze({
  ClientToServer: "ClientToServer",
  ServerToClient: "ServerToClient",
});

export const OperationQueueStatus = Object.freeze({
  Enqueued: "Enqueued",
  Invalid: "Invalid",
  Unauthorized: "Unauthorized",
  NotReady: "NotReady",
  QueueFull: "QueueFull",
  SequenceGap: "SequenceGap",
  SequenceConflict: "SequenceConflict",
  Expired: "Expired",
  Completed: "Completed",
});

export const OperationExecutionStatus = Object.freeze({
  Unspecified: "Unspecified",
  Succeeded: "Succeeded",
  Rejected: "Rejected",
  Aborted: "Aborted",
  Unknown: "Unknown",
});

export const SnapshotAcknowledgement = Object.freeze({
  Waiting: "Waiting",
  Started: "Started",
  Stale: "Stale",
});

const operationQueueStatusValues = [
  "Enqueued", "Invalid", "Unauthorized", "NotReady", "QueueFull",
  "SequenceGap", "SequenceConflict", "Expired", "Completed",
];
const operationExecutionStatusValues = [
  "Unspecified", "Succeeded", "Rejected", "Aborted", "Unknown",
];
const snapshotAcknowledgementValues = ["Waiting", "Started", "Stale"];

const contracts = new Map([
  ["move", { type: "move", direction: EventDirection.ClientToServer }],
  ["move.stop", { type: "stopMove", direction: EventDirection.ClientToServer }],
  ["ability.cast", { type: "castAbility", direction: EventDirection.ClientToServer }],
  ["operation.result", { type: "operationResult", direction: EventDirection.ServerToClient }],
  ["projectile.fact", { type: "projectileFact", direction: EventDirection.ServerToClient }],
]);

function assertPosition(value, context) {
  const position = assertObject(value, `${context} must be an object.`);
  assertKnownKeys(position, ["x", "y"], context);
  if (typeof position.x !== "number" || !Number.isFinite(position.x) ||
      typeof position.y !== "number" || !Number.isFinite(position.y))
    throw new ObservationError(`${context}.x and ${context}.y must be finite numbers.`);
  return { x: position.x, y: position.y };
}

function assertGuid(value, context) {
  if (!guid(value)) throw new ObservationError(`${context} must be a GUID.`);
  return normalizeGuid(value);
}

export function moveCommand(command) {
  const value = assertObject(command, "Move command must be an object.");
  assertKnownKeys(value, ["unitId", "mapInstanceId", "controlVersion", "destination"], "move command");
  if (!Number.isSafeInteger(value.controlVersion) || value.controlVersion < 0)
    throw new TypeError("controlVersion must be a non-negative integer.");
  return {
    unitId: assertGuid(value.unitId, "unitId"),
    mapInstanceId: assertGuid(value.mapInstanceId, "mapInstanceId"),
    controlVersion: value.controlVersion,
    destination: assertPosition(value.destination, "destination"),
  };
}

export function stopMoveCommand(command) {
  const value = assertObject(command, "Stop-move command must be an object.");
  assertKnownKeys(value, ["unitId", "mapInstanceId", "controlVersion"], "stop-move command");
  if (!Number.isSafeInteger(value.controlVersion) || value.controlVersion < 0)
    throw new TypeError("controlVersion must be a non-negative integer.");
  return {
    unitId: assertGuid(value.unitId, "unitId"),
    mapInstanceId: assertGuid(value.mapInstanceId, "mapInstanceId"),
    controlVersion: value.controlVersion,
  };
}

export function castAbilityCommand(command) {
  const value = assertObject(command, "Cast-ability command must be an object.");
  assertKnownKeys(value, ["unitId", "mapInstanceId", "controlVersion", "abilityInstanceId",
    "targetUnitId", "targetPoint"], "cast-ability command");
  if (!Number.isSafeInteger(value.controlVersion) || value.controlVersion < 0)
    throw new TypeError("controlVersion must be a non-negative integer.");
  const result = {
    unitId: assertGuid(value.unitId, "unitId"),
    mapInstanceId: assertGuid(value.mapInstanceId, "mapInstanceId"),
    controlVersion: value.controlVersion,
    abilityInstanceId: assertGuid(value.abilityInstanceId, "abilityInstanceId"),
  };
  if (value.targetUnitId !== undefined) result.targetUnitId = assertGuid(value.targetUnitId, "targetUnitId");
  if (value.targetPoint !== undefined) result.targetPoint = assertPosition(value.targetPoint, "targetPoint");
  return result;
}

export function inferClientEvent(message) {
  const value = assertObject(message, "Client event must be an object.");
  if (value.eventId && typeof value.eventId === "string") {
    if (value.eventId === "move") return { eventId: "move", payload: moveCommand(value) };
    if (value.eventId === "move.stop") return { eventId: "move.stop", payload: stopMoveCommand(value) };
    if (value.eventId === "ability.cast") return { eventId: "ability.cast", payload: castAbilityCommand(value) };
  }
  if (value.destination !== undefined) return { eventId: "move", payload: moveCommand(value) };
  if (value.targetUnitId !== undefined || value.targetPoint !== undefined || value.abilityInstanceId !== undefined)
    return { eventId: "ability.cast", payload: castAbilityCommand(value) };
  if (value.unitId !== undefined && value.mapInstanceId !== undefined)
    return { eventId: "move.stop", payload: stopMoveCommand(value) };
  throw new ProtocolError("Cannot infer a client event type.");
}

export function encodeClientEvent(message) {
  const inferred = inferClientEvent(message);
  const payload = yaml.dump(inferred.payload, {
    flowLevel: -1,
    noRefs: true,
    lineWidth: -1,
  });
  return {
    version: EVENT_ENVELOPE_VERSION,
    eventId: inferred.eventId,
    correlationId: randomUuid(),
    payload,
  };
}

export function decodeServerEvent(envelope) {
  if (!isPlainObject(envelope)) throw new ObservationError("Event envelope must be an object.");
  if (envelope.version !== EVENT_ENVELOPE_VERSION)
    throw new ObservationError(`Unsupported event version ${envelope.version}.`);
  if (typeof envelope.eventId !== "string" || typeof envelope.payload !== "string")
    throw new ObservationError("Event envelope requires eventId and payload.");
  const contract = contracts.get(envelope.eventId);
  if (!contract || contract.direction !== EventDirection.ServerToClient)
    throw new ObservationError(`Unknown event identifier or forbidden event direction: ${envelope.eventId}.`);
  let payload;
  try {
    payload = yaml.load(envelope.payload, { json: true });
  } catch (error) {
    throw new ObservationError("The event payload is malformed YAML.", error);
  }
  assertObject(payload, "Event payload must be an object.");
  if (contract.type === "operationResult") {
    assertKnownKeys(payload, ["operationSequence", "correlationId", "status", "reason"], "operation.result");
    if (!Number.isSafeInteger(payload.operationSequence) || payload.operationSequence < 1)
      throw new ObservationError("operationSequence must be a positive safe integer.");
    if (!guid(payload.correlationId))
      throw new ObservationError("Operation result correlationId must be a GUID.");
    const status = normalizeEnum(
      payload.status,
      operationExecutionStatusValues,
      "operation result status",
    );
    if (!["Succeeded", "Rejected", "Aborted", "Unknown"].includes(status) || status === "Unspecified")
      throw new ObservationError("Operation result status is invalid.");
    return deepResult({
      eventId: envelope.eventId,
      operationSequence: payload.operationSequence,
      correlationId: normalizeGuid(payload.correlationId),
      status,
      reason: payload.reason ?? null,
    });
  }
  assertKnownKeys(payload, ["eventSequence", "time", "kind", "projectileId", "mapInstanceId",
    "sourceUnitId", "targetUnitId", "abilityId", "abilityInstanceId", "position", "destination"], "projectile.fact");
  if (!Number.isSafeInteger(payload.eventSequence) || payload.eventSequence < 0)
    throw new ObservationError("Projectile fact eventSequence is invalid.");
  const kind = normalizeProjectileFactKind(payload.kind);
  const fact = {
    eventId: envelope.eventId,
    eventSequence: payload.eventSequence,
    time: payload.time,
    kind,
    projectileId: assertGuid(payload.projectileId, "projectileId"),
    mapInstanceId: assertGuid(payload.mapInstanceId, "mapInstanceId"),
    sourceUnitId: assertGuid(payload.sourceUnitId, "sourceUnitId"),
    abilityId: payload.abilityId,
    position: assertPosition(payload.position, "position"),
  };
  if (payload.targetUnitId !== undefined && payload.targetUnitId !== null)
    fact.targetUnitId = assertGuid(payload.targetUnitId, "targetUnitId");
  if (payload.abilityInstanceId !== undefined && payload.abilityInstanceId !== null)
    fact.abilityInstanceId = assertGuid(payload.abilityInstanceId, "abilityInstanceId");
  if (payload.destination !== undefined && payload.destination !== null)
    fact.destination = assertPosition(payload.destination, "destination");
  return fact;
}

function deepResult(value) {
  return Object.freeze(clone(value));
}

function isPlainObject(value) {
  return value !== null && typeof value === "object" &&
    (Object.getPrototypeOf(value) === Object.prototype || Object.getPrototypeOf(value) === null);
}

export function randomUuid() {
  if (typeof globalThis.crypto?.randomUUID === "function") return globalThis.crypto.randomUUID();
  if (typeof globalThis.crypto?.getRandomValues === "function") {
    const bytes = new Uint8Array(16);
    globalThis.crypto.getRandomValues(bytes);
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  }
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, character => {
    const random = Math.floor(Math.random() * 16);
    return (character === "x" ? random : (random & 0x3) | 0x8).toString(16);
  });
}

export function normalizeEnum(value, values, context) {
  if (typeof value === "string") {
    if (values.includes(value)) return value;
  } else if (Number.isInteger(value) && value >= 0 && value < values.length) {
    return values[value];
  }
  throw new ObservationError(`Invalid ${context}: ${value}.`);
}

export function normalizeProjectileFactKind(value) {
  if (typeof value === "number") {
    if (value === 1) return "Emitted";
    if (value === 2) return "Hit";
    if (value === 3) return "Explosion";
  }
  if (typeof value === "string") {
    if (["Emitted", "Hit", "Explosion"].includes(value)) return value;
  }
  throw new ObservationError(`Invalid projectile fact kind: ${value}.`);
}

export function normalizeOperationQueueStatus(value) {
  return normalizeEnum(value, operationQueueStatusValues, "operation queue status");
}

export function normalizeSnapshotAcknowledgement(value) {
  return normalizeEnum(value, snapshotAcknowledgementValues, "snapshot acknowledgement");
}
