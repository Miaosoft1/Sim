import { ObservationError } from "./errors.js";
import { deepFreeze, guid, normalizeGuid } from "./utils.js";
import { decodeServerEvent, normalizeEnum } from "./protocol.js";

export class ClientWorld {
  constructor(moduleCodecs = []) {
    this.modules = new Map();
    for (const module of moduleCodecs) {
      if (!module?.id || !Number.isInteger(module.version) || module.version < 1)
        throw new TypeError("Client observation modules require id and a positive version.");
      if (this.modules.has(module.id)) throw new TypeError(`Duplicate observation module '${module.id}'.`);
      this.modules.set(module.id, module);
    }
    this.published = null;
  }

  get state() {
    if (!this.published) throw new Error("No session world has been prepared.");
    return this.published;
  }

  get capabilities() {
    return deepFreeze(Array.from(this.modules.values(), module => ({
      id: module.id,
      version: module.version,
    })));
  }

  prepare(snapshot) {
    if (!snapshot || typeof snapshot !== "object")
      throw new ObservationError("Prepared snapshot is required.");
    const frame = snapshot.frame;
    if (!snapshot.snapshotId || !snapshot.binding || !frame?.isFull)
      throw new ObservationError("The prepared session snapshot is invalid.");
    if (frame.connectionVersion !== snapshot.binding.connectionVersion)
      throw new ObservationError("Prepared snapshot connection versions disagree.");
    if (!Number.isSafeInteger(snapshot.nextOperationSequence) || snapshot.nextOperationSequence < 1)
      throw new ObservationError("nextOperationSequence is invalid.");
    if (!snapshot.snapshotId || typeof snapshot.snapshotId !== "string")
      throw new ObservationError("Prepared snapshot snapshotId is invalid.");
    if (this.published) {
      const previous = this.published;
      if (previous.binding.sessionId !== snapshot.binding.sessionId ||
          previous.binding.participantId !== snapshot.binding.participantId ||
          snapshot.binding.connectionVersion < previous.binding.connectionVersion)
        throw new ObservationError("A prepared snapshot cannot change participant identity or restore an older connection.");
      if (snapshot.binding.connectionVersion === previous.binding.connectionVersion &&
          (frame.publicationSequence < previous.publicationSequence ||
           frame.stateVersion < previous.stateVersion))
        throw new ObservationError("A prepared snapshot cannot restore an older publication.");
      if (previous.world.isCompleted && frame.world?.isCompleted === false)
        throw new ObservationError("Recovery cannot restart a completed session.");
    }
    const next = this.buildFrame(frame, snapshot.binding, null);
    this.published = next;
    return { snapshot, nextOperationSequence: snapshot.nextOperationSequence };
  }

  apply(frame) {
    if (!frame || typeof frame !== "object") throw new ObservationError("Observation frame is required.");
    const previous = this.state;
    if (frame.connectionVersion < previous.binding.connectionVersion) return "StaleConnection";
    if (frame.connectionVersion === previous.binding.connectionVersion &&
        frame.publicationSequence <= previous.publicationSequence &&
        frame.stateVersion <= previous.stateVersion) return "SupersededPublication";
    if (frame.isFull)
      throw new ObservationError("A live full replacement requires a fresh snapshot.");
    if (frame.connectionVersion !== previous.binding.connectionVersion ||
        frame.publicationSequence !== previous.publicationSequence + 1 ||
        frame.stateVersion <= previous.stateVersion)
      throw new ObservationError("The observation stream is discontinuous; a fresh snapshot is required.");
    if (previous.world.isCompleted && frame.world?.isCompleted === false)
      throw new ObservationError("Recovery cannot restart a completed session.");
    this.published = this.buildFrame(frame, {
      ...previous.binding,
      unitId: frame.controlledUnitId ?? null,
    }, previous);
    return "Applied";
  }

  buildFrame(frame, binding, previous) {
    validateFrameHeader(frame);
    validateWorld(frame.world);
    validateModules(frame.modules, frame.isFull, this.modules);

    const moduleStates = previous
      ? new Map(Object.entries(previous.modules))
      : new Map();

    for (const update of frame.modules ?? []) {
      const module = this.modules.get(update.id);
      if (!module) throw new ObservationError(`Unknown observation module '${update.id}'.`);
      if (module.version !== update.version)
        throw new ObservationError(`Observation module '${update.id}' has an incompatible version.`);
      if (update.stateVersion !== frame.stateVersion)
        throw new ObservationError(`Observation module '${update.id}' crossed a state boundary.`);
      const kind = normalizeEnum(update.kind, ["Full", "Delta", "Removed"], "module update kind");
      if (kind === "Full") {
        if (typeof module.readFull !== "function")
          throw new ObservationError(`The module '${update.id}' has no full codec.`);
        moduleStates.set(update.id, module.readFull(update.payload, binding));
      } else if (kind === "Delta") {
        if (frame.isFull || !moduleStates.has(update.id))
          throw new ObservationError(`Module delta '${update.id}' requires established state.`);
        if (typeof module.applyDelta !== "function")
          throw new ObservationError(`The module '${update.id}' has no delta codec.`);
        moduleStates.set(update.id, module.applyDelta(moduleStates.get(update.id), update.payload, binding));
      } else if (kind === "Removed") {
        if (update.payload !== "")
          throw new ObservationError(`Module removal '${update.id}' cannot carry payload.`);
        moduleStates.delete(update.id);
      } else {
        throw new ObservationError(`Unknown module update kind '${update.kind}'.`);
      }
    }

    const events = (frame.events ?? []).map(decodeServerEvent);
    if (frame.isFull && events.some(event => event.eventId === "projectile.fact"))
      throw new ObservationError("Transient projectile facts cannot be replayed in a full snapshot.");

    const world = cloneDetached(frame.world);
    const state = deepFreeze({
      binding: deepFreeze(clonedBinding(binding)),
      stateVersion: frame.stateVersion,
      publicationSequence: frame.publicationSequence,
      world,
      modules: deepFreeze(Object.fromEntries(moduleStates)),
      events: deepFreeze(events),
    });
    return state;
  }
}

export function clonedBinding(binding) {
  if (!binding?.sessionId || !binding?.participantId)
    throw new ObservationError("Session binding identity is incomplete.");
  if (!Number.isSafeInteger(binding.connectionVersion) || binding.connectionVersion < 1)
    throw new ObservationError("Session binding connectionVersion is invalid.");
  return {
    sessionId: binding.sessionId,
    participantId: binding.participantId,
    unitId: binding.unitId ?? null,
    connectionVersion: binding.connectionVersion,
  };
}

function validateFrameHeader(frame) {
  if (!Number.isSafeInteger(frame.stateVersion) || frame.stateVersion < 0 ||
      !Number.isSafeInteger(frame.publicationSequence) || frame.publicationSequence < 1 ||
      !Number.isSafeInteger(frame.connectionVersion) || frame.connectionVersion < 1 ||
      typeof frame.isFull !== "boolean" || !frame.world)
    throw new ObservationError("The observation frame header is invalid.");
  if (!Array.isArray(frame.modules) || !Array.isArray(frame.events))
    throw new ObservationError("The observation frame lists are invalid.");
}

function validateWorld(world) {
  if (!world || typeof world !== "object" || !Array.isArray(world.units) ||
      !Array.isArray(world.maps) || !Array.isArray(world.projectiles))
    throw new ObservationError("The world snapshot is invalid.");
  if (typeof world.time !== "string" || typeof world.timeScale !== "number" ||
      !Number.isFinite(world.timeScale) || world.timeScale < 0)
    throw new ObservationError("The world time is invalid.");
  const unitIds = new Set();
  for (const unit of world.units) {
    if (!unit || !guid(unit.id)) throw new ObservationError("The world contains an invalid unit.");
    const unitId = normalizeGuid(unit.id);
    if (unitIds.has(unitId)) throw new ObservationError("Duplicate unit identity.");
    unitIds.add(unitId);
  }
}

function validateModules(updates, isFull, modules) {
  const ids = new Set();
  for (const update of updates) {
    if (!update || typeof update.id !== "string" || ids.has(update.id))
      throw new ObservationError("The observation frame duplicated or omitted a module.");
    ids.add(update.id);
  }
  if (isFull) {
    const expected = new Set(modules.keys());
    if (ids.size !== expected.size || [...ids].some(id => !expected.has(id)))
      throw new ObservationError("A full frame omitted or duplicated a negotiated module.");
  }
}

function cloneDetached(value) {
  if (Array.isArray(value)) return deepFreeze(value.map(cloneDetached));
  if (value !== null && typeof value === "object") {
    const copy = {};
    for (const [key, child] of Object.entries(value)) copy[key] = cloneDetached(child);
    return deepFreeze(copy);
  }
  return value;
}
