import { describe, expect, it } from "vitest";
import yaml from "js-yaml";
import {
  castAbilityCommand,
  decodeServerEvent,
  encodeClientEvent,
  moveCommand,
  stopMoveCommand,
  normalizeProjectileFactKind,
} from "../src/protocol.js";

describe("event codec", () => {
  it("maps projectile fact kinds including numeric wire values", () => {
    expect(normalizeProjectileFactKind(1)).toBe("Emitted");
    expect(normalizeProjectileFactKind(2)).toBe("Hit");
    expect(normalizeProjectileFactKind(3)).toBe("Explosion");
  });
  it("encodes and decodes an operation result envelope", () => {
    const correlationId = "11111111-1111-1111-1111-111111111111";
    const envelope = {
      version: 2,
      eventId: "operation.result",
      payload: yaml.dump({
        operationSequence: 17,
        correlationId,
        status: "Succeeded",
        reason: "explicit execution disposition",
      }),
    };

    expect(decodeServerEvent(envelope)).toEqual({
      eventId: "operation.result",
      operationSequence: 17,
      correlationId,
      status: "Succeeded",
      reason: "explicit execution disposition",
    });
  });

  it("rejects server-only outcomes on client event encoding", () => {
    expect(() => encodeClientEvent({
      eventId: "operation.result",
      operationSequence: 17,
      correlationId: "11111111-1111-1111-1111-111111111111",
      status: "Succeeded",
    })).toThrow();
  });

  it("preserves actual ability and target identities", () => {
    const unitId = "11111111-1111-1111-1111-111111111111";
    const mapId = "22222222-2222-2222-2222-222222222222";
    const abilityId = "33333333-3333-3333-3333-333333333333";
    const targetId = "44444444-4444-4444-4444-444444444444";
    const envelope = encodeClientEvent(castAbilityCommand({
      unitId,
      mapInstanceId: mapId,
      controlVersion: 4,
      abilityInstanceId: abilityId,
      targetUnitId: targetId,
    }));

    expect(envelope.eventId).toBe("ability.cast");
    expect(envelope.version).toBe(2);
    expect(envelope.correlationId).toMatch(/^[0-9a-f-]{36}$/i);
    expect(yaml.load(envelope.payload)).toMatchObject({
      unitId,
      mapInstanceId: mapId,
      controlVersion: 4,
      abilityInstanceId: abilityId,
      targetUnitId: targetId,
    });
  });

  it("builds move and stop move commands", () => {
    const unitId = "11111111-1111-1111-1111-111111111111";
    const mapId = "22222222-2222-2222-2222-222222222222";
    expect(moveCommand({
      unitId,
      mapInstanceId: mapId,
      controlVersion: 3,
      destination: { x: 14, y: 10 },
    }).destination).toEqual({ x: 14, y: 10 });
    expect(stopMoveCommand({
      unitId,
      mapInstanceId: mapId,
      controlVersion: 3,
    }).controlVersion).toBe(3);
  });
});
