import { describe, expect, it } from "vitest";
import { ObservationError } from "../src/errors.js";
import { ClientWorld } from "../src/world.js";

const sessionId = "11111111-1111-1111-1111-111111111111";
const participantId = "22222222-2222-2222-2222-222222222222";
const unitId = "33333333-3333-3333-3333-333333333333";

function snapshot(overrides = {}) {
  return {
    snapshotId: "44444444-4444-4444-4444-444444444444",
    binding: {
      sessionId,
      participantId,
      unitId,
      connectionVersion: 1,
    },
    nextOperationSequence: 1,
    frame: frame({ isFull: true, ...overrides }),
  };
}

function frame(overrides = {}) {
  return {
    stateVersion: 10,
    publicationSequence: 1,
    connectionVersion: 1,
    controlledUnitId: unitId,
    isFull: true,
    world: {
      isCompleted: false,
      time: "00:00:00",
      timeScale: 1,
      units: [],
      maps: [],
      projectiles: [],
    },
    modules: [],
    events: [],
    ...overrides,
  };
}

describe("ClientWorld", () => {
  it("prepares and freezes a full snapshot", () => {
    const world = new ClientWorld();
    world.prepare(snapshot());
    expect(world.state.stateVersion).toBe(10);
    expect(Object.isFrozen(world.state)).toBe(true);
    expect(Object.isFrozen(world.state.world)).toBe(true);
  });

  it("accepts consecutive live frames and rejects superseded frames", () => {
    const world = new ClientWorld();
    world.prepare(snapshot());
    expect(world.apply(frame({
      isFull: false,
      stateVersion: 11,
      publicationSequence: 2,
    }))).toBe("Applied");
    expect(world.apply(frame({
      isFull: false,
      stateVersion: 10,
      publicationSequence: 1,
    }))).toBe("SupersededPublication");
  });

  it("rejects a live full frame and transient facts in full snapshots", () => {
    const world = new ClientWorld();
    expect(() => world.apply(frame())).toThrow(/No session world/);
    world.prepare(snapshot());
    expect(() => world.apply(frame({
      isFull: true,
      stateVersion: 11,
      publicationSequence: 2,
    }))).toThrow(/fresh snapshot/i);
  });

  it("rejects a completed session restarting", () => {
    const world = new ClientWorld();
    world.prepare(snapshot());
    world.apply(frame({
      isFull: false,
      stateVersion: 11,
      publicationSequence: 2,
      world: {
        isCompleted: true,
        time: "00:00:01",
        timeScale: 0,
        units: [],
        maps: [],
        projectiles: [],
      },
    }));
    expect(() => world.apply(frame({
      isFull: false,
      stateVersion: 12,
      publicationSequence: 3,
      world: {
        isCompleted: false,
        time: "00:00:02",
        timeScale: 1,
        units: [],
        maps: [],
        projectiles: [],
      },
    }))).toThrow(ObservationError);
  });
});
