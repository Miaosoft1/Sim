import { describe, expect, it } from "vitest";
import yaml from "js-yaml";
import { GameClient } from "../src/game-client.js";

const sessionId = "11111111-1111-1111-1111-111111111111";
const participantId = "22222222-2222-2222-2222-222222222222";
const unitId = "33333333-3333-3333-3333-333333333333";
const mapId = "44444444-4444-4444-4444-444444444444";

function preparedSnapshot() {
  return {
    snapshotId: "55555555-5555-5555-5555-555555555555",
    binding: { sessionId, participantId, unitId, connectionVersion: 1 },
    nextOperationSequence: 1,
    frame: {
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
    },
  };
}

class FakeTransport {
  constructor(responses = {}) {
    this.responses = responses;
    this.handlers = new Map();
    this.closed = null;
    this.submissions = [];
  }

  async start() {}
  async stop() {}
  on(name, handler) { this.handlers.set(name, handler); }
  onClosed(handler) { this.closed = handler; }
  async invoke(method, ...args) {
    if (method === "EnterDemo") {
      return {
        credentials: { sessionId, participantId, token: "test-token" },
        snapshot: preparedSnapshot(),
      };
    }
    if (method === "AcknowledgeSnapshot") return "Waiting";
    if (method === "SubmitEvents") {
      const batch = args[0];
      this.submissions.push(batch);
      if (this.responses.submitException) throw this.responses.submitException;
      if (this.responses.submitQueue) return this.responses.submitQueue(this.submissions.length);
      return [{ sequence: batch.operations[0].sequence, status: "Enqueued", nextOperationSequence: 2 }];
    }
    if (method === "Rejoin") throw new Error("Unexpected rejoin.");
    throw new Error(`Unexpected method ${method}`);
  }

  receiveFrame(frame) {
    this.handlers.get("ReceiveFrame")(frame);
  }
}

describe("GameClient", () => {
  it("admits, retains the original operation, and retries the same envelope", async () => {
    const transport = new FakeTransport({
      submitException: null,
      submitQueue: count => count === 1
        ? [{ sequence: 1, status: "NotReady", nextOperationSequence: null, reason: "test" }]
        : [{ sequence: 1, status: "Enqueued", nextOperationSequence: 2 }],
    });
    const client = new GameClient({ transport, endpoint: "http://127.0.0.1:5050/session" });
    await client.enterDemo();

    const receipt = await client.send({
      unitId,
      mapInstanceId: mapId,
      controlVersion: 1,
      destination: { x: 14, y: 10 },
    });
    expect(receipt.status).toBe("NotReady");
    expect(client.pending.operation.sequence).toBe(1);

    const retried = await client.retryPending();
    expect(retried.status).toBe("Enqueued");
    expect(transport.submissions).toHaveLength(2);
    expect(transport.submissions[0].operations[0]).toEqual(transport.submissions[1].operations[0]);
  });

  it("marks transport uncertainty and never silently advances or resubmits", async () => {
    let submissions = 0;
    const transport = new FakeTransport({
      submitException: null,
      submitQueue: () => {
        submissions++;
        if (submissions === 1) throw new Error("transport uncertain");
        return [{ sequence: 1, status: "Enqueued", nextOperationSequence: 2 }];
      },
    });
    const client = new GameClient({ transport });
    await client.enterDemo("http://127.0.0.1:5050/session");

    await expect(client.send({
      unitId,
      mapInstanceId: mapId,
      controlVersion: 1,
    })).rejects.toThrow("transport uncertain");
    expect(client.pending.uncertain).toBe(true);
    expect(client.pending.operation.sequence).toBe(1);
    await expect(client.send({
      unitId,
      mapInstanceId: mapId,
      controlVersion: 1,
    })).rejects.toThrow(/pending original/i);

    await client.retryPending();
    expect(transport.submissions).toHaveLength(2);
  });

  it("waits for a typed business outcome from an observation frame", async () => {
    const transport = new FakeTransport();
    const client = new GameClient({ transport });
    await client.enterDemo("http://127.0.0.1:5050/session");

    const waiting = client.waitForOutcome(1);
    transport.receiveFrame({
      stateVersion: 11,
      publicationSequence: 2,
      connectionVersion: 1,
      controlledUnitId: unitId,
      isFull: false,
      world: {
        isCompleted: false,
        time: "00:00:01",
        timeScale: 1,
        units: [],
        maps: [],
        projectiles: [],
      },
      modules: [],
      events: [{
        version: 2,
        eventId: "operation.result",
        correlationId: null,
        payload: yaml.dump({
          operationSequence: 1,
          correlationId: "11111111-1111-1111-1111-111111111111",
          status: "Succeeded",
          reason: null,
        }),
      }],
    });

    await expect(waiting).resolves.toMatchObject({
      operationSequence: 1,
      status: "Succeeded",
    });
  });
});
