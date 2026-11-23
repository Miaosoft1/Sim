import { ObservationError, ProtocolError } from "./errors.js";
import { encodeClientEvent } from "./protocol.js";
import {
  SESSION_PROTOCOL_VERSION,
  OPERATION_RECEIPT_WINDOW,
  normalizeOperationQueueStatus,
  normalizeSnapshotAcknowledgement,
} from "./protocol.js";
import { ClientWorld } from "./world.js";

export class GameClient {
  constructor(options = {}) {
    if (!options.transport) throw new TypeError("A session transport is required.");
    this.transport = options.transport;
    this.world = new ClientWorld(options.observationModules ?? []);
    this.credentials = null;
    this.endpoint = options.endpoint ?? null;
    this.nextSequence = 1;
    this.pending = null;
    this.outcomes = new Map();
    this.observationFailure = null;
    this.listeners = new Set();
    this.connectionLostListeners = new Set();
    this.observationRejectedListeners = new Set();
    this.changeWaiters = new Set();
    this.transportEpoch = 0;

    this.transport.on("ReceiveFrame", frame => this.receiveFrame(frame));
    this.transport.onClosed(error => {
      for (const listener of this.connectionLostListeners) listener(error);
    });
  }

  onObservation(listener) { this.listeners.add(listener); return () => this.listeners.delete(listener); }
  onConnectionLost(listener) { this.connectionLostListeners.add(listener); return () => this.connectionLostListeners.delete(listener); }
  onObservationRejected(listener) { this.observationRejectedListeners.add(listener); return () => this.observationRejectedListeners.delete(listener); }

  async enterDemo(endpoint = this.endpoint) {
    if (this.credentials) throw new Error("Use rejoin for an existing participant.");
    if (!endpoint) throw new Error("A session endpoint is required.");
    await this.transport.start(endpoint);
    this.endpoint = endpoint;
    const welcome = await this.transport.invoke("EnterDemo", {
      protocolVersion: SESSION_PROTOCOL_VERSION,
      requestId: cryptoUuid(),
      modules: this.world.capabilities,
    });
    validateWelcome(welcome);
    this.credentials = welcome.credentials;
    const prepared = this.world.prepare(welcome.snapshot);
    this.nextSequence = Math.max(this.nextSequence, prepared.nextOperationSequence);
    await this.acknowledge(welcome.snapshot);
    this.publishObservation();
    return welcome;
  }

  async rejoin(endpoint = this.endpoint, credentials = this.credentials) {
    if (!credentials) throw new Error("A server-issued rejoin credential is required.");
    if (!endpoint) throw new Error("A session endpoint is required.");
    if (this.credentials && (this.credentials.sessionId !== credentials.sessionId ||
        this.credentials.participantId !== credentials.participantId))
      throw new Error("Use a new client instance for a different participant.");
    await this.transport.start(endpoint);
    this.endpoint = endpoint;
    const snapshot = await this.transport.invoke("Rejoin", {
      protocolVersion: SESSION_PROTOCOL_VERSION,
      credentials,
      modules: this.world.capabilities,
    });
    if (snapshot.binding.sessionId !== credentials.sessionId ||
        snapshot.binding.participantId !== credentials.participantId)
      throw new ObservationError("Rejoin returned another participant's snapshot.");
    this.credentials = credentials;
    const prepared = this.world.prepare(snapshot);
    this.nextSequence = Math.max(this.nextSequence, prepared.nextOperationSequence);
    const acknowledged = await this.acknowledge(snapshot);
    this.publishObservation();
    return acknowledged;
  }

  async refreshSnapshot() {
    const snapshot = await this.transport.invoke(
      "RequestSnapshot",
      this.world.state.binding.connectionVersion,
    );
    const prepared = this.world.prepare(snapshot);
    this.nextSequence = Math.max(this.nextSequence, prepared.nextOperationSequence);
    const acknowledged = await this.acknowledge(snapshot);
    this.publishObservation();
    return acknowledged;
  }

  async send(message) {
    if (this.pending) throw new Error("Retry or clear the pending original operation before sending another.");
    if (this.nextSequence === Number.MAX_SAFE_INTEGER)
      throw new Error("The operation sequence has been exhausted.");
    const operation = {
      sequence: this.nextSequence,
      event: encodeClientEvent(message),
    };
    this.pending = { operation, uncertain: false, lastReceipt: null };
    return this.sendPending();
  }

  async retryPending() {
    if (!this.pending) throw new Error("There is no retained operation to retry.");
    return this.sendPending();
  }

  async clearUnqueuedPending() {
    if (!this.pending) return;
    if (this.pending.uncertain || !this.pending.lastReceipt ||
        ["Enqueued", "Expired"].includes(this.pending.lastReceipt.status))
      throw new Error("An uncertain or accepted operation cannot be silently discarded or resubmitted under a new number.");
    this.pending = null;
  }

  async submitBatch(batch) {
    if (this.pending) throw new Error("A retained operation must be resolved first.");
    const receipts = await this.transport.invoke("SubmitEvents", batch);
    if (!Array.isArray(receipts) || receipts.length !== batch.operations.length)
      throw new ObservationError("The server returned a mismatched batch acknowledgement.");
    receipts.forEach((receipt, index) => {
      const operation = batch.operations[index];
      if (!receipt || receipt.sequence !== operation.sequence)
        throw new ObservationError("The server returned a mismatched batch acknowledgement.");
      receipt.status = normalizeOperationQueueStatus(receipt.status);
      if (receipt.status === "Enqueued") {
        if (!Number.isSafeInteger(receipt.nextOperationSequence) ||
            receipt.nextOperationSequence <= receipt.sequence)
          throw new ObservationError("Queue acceptance omitted its next operation sequence.");
        this.nextSequence = Math.max(this.nextSequence, receipt.nextOperationSequence);
      }
    });
    return receipts;
  }

  async waitForOutcome(sequence) {
    if (!Number.isSafeInteger(sequence) || sequence < 1)
      throw new TypeError("Operation sequence must be a positive integer.");
    while (true) {
      if (this.outcomes.has(sequence)) return this.outcomes.get(sequence);
      if (this.observationFailure)
        throw new ObservationError("Observation synchronization failed.", this.observationFailure);
      await new Promise(resolve => this.changeWaiters.add(resolve));
    }
  }

  async disconnect() {
    await this.transport.stop();
  }

  async acknowledge(snapshot) {
    let current = snapshot;
    while (true) {
      const acknowledgement = await this.transport.invoke("AcknowledgeSnapshot", {
        connectionVersion: current.binding.connectionVersion,
        snapshotId: current.snapshotId,
      });
      if (normalizeSnapshotAcknowledgement(acknowledgement) !== "Stale") return current;
      current = await this.transport.invoke(
        "RequestSnapshot",
        current.binding.connectionVersion,
      );
      const prepared = this.world.prepare(current);
      this.nextSequence = Math.max(this.nextSequence, prepared.nextOperationSequence);
    }
  }

  async sendPending() {
    const original = this.pending;
    let received = false;
    try {
      const binding = this.world.state.binding;
      const receipts = await this.transport.invoke("SubmitEvents", {
        participantId: binding.participantId,
        connectionVersion: binding.connectionVersion,
        operations: [original.operation],
      });
      if (!Array.isArray(receipts) || receipts.length !== 1 ||
          receipts[0].sequence !== original.operation.sequence)
        throw new ObservationError("The server returned a mismatched operation acknowledgement.");
      received = true;
      const receipt = receipts[0];
      receipt.status = normalizeOperationQueueStatus(receipt.status);
      if (receipt.status === "Enqueued") {
        if (!Number.isSafeInteger(receipt.nextOperationSequence) ||
            receipt.nextOperationSequence <= original.operation.sequence)
          throw new ObservationError("Queue acceptance omitted its next operation sequence.");
        this.nextSequence = Math.max(this.nextSequence, receipt.nextOperationSequence);
        this.pending = null;
      } else {
        this.pending = { ...original, lastReceipt: receipt };
      }
      return receipt;
    } finally {
      if (!received) this.pending = { ...original, uncertain: true };
    }
  }

  receiveFrame(frame) {
    try {
      const result = this.world.apply(frame);
      if (result === "Applied") this.publishObservation();
    } catch (error) {
      this.observationFailure = error;
      for (const listener of this.observationRejectedListeners) listener(error);
      this.signalWaiters();
    }
  }

  publishObservation() {
    this.observationFailure = null;
    for (const event of this.world.state.events) {
      if (event.eventId !== "operation.result") continue;
      const existing = this.outcomes.get(event.operationSequence);
      if (existing && JSON.stringify(existing) !== JSON.stringify(event))
        throw new ObservationError("The server published conflicting business outcomes.");
      this.outcomes.set(event.operationSequence, event);
      while (this.outcomes.size > OPERATION_RECEIPT_WINDOW) {
        const smallest = Math.min(...this.outcomes.keys());
        this.outcomes.delete(smallest);
      }
    }
    for (const listener of this.listeners) listener(this.world.state);
    this.signalWaiters();
  }

  signalWaiters() {
    for (const resolve of this.changeWaiters) resolve();
    this.changeWaiters.clear();
  }
}

function validateWelcome(welcome) {
  if (!welcome?.credentials || !welcome?.snapshot)
    throw new ObservationError("The server admission response is incomplete.");
  if (welcome.credentials.sessionId !== welcome.snapshot.binding.sessionId ||
      welcome.credentials.participantId !== welcome.snapshot.binding.participantId)
    throw new ObservationError("The admission credential belongs to another snapshot.");
}

function cryptoUuid() {
  if (typeof globalThis.crypto?.randomUUID === "function") return globalThis.crypto.randomUUID();
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, character => {
    const random = Math.floor(Math.random() * 16);
    return (character === "x" ? random : (random & 0x3) | 0x8).toString(16);
  });
}
