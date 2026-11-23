import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";

export class SignalRTransport {
  constructor(url, options = {}) {
    this.url = url;
    this.options = options;
    const absolute = new URL(url);
    if (!["http:", "https:"].includes(absolute.protocol))
      throw new TypeError("Session endpoints require HTTP or HTTPS.");
    if (absolute.protocol === "http:" &&
        !["localhost", "127.0.0.1", "::1"].includes(absolute.hostname))
      throw new TypeError("Anonymous demo endpoints must use loopback HTTP.");
    this.connection = new HubConnectionBuilder()
      .withUrl(absolute.toString())
      .withAutomaticReconnect(this.options.automaticReconnect ?? [])
      .configureLogging(this.options.logLevel ?? LogLevel.Warning)
      .build();
  }

  async start(url = this.url) {
    if (this.connection?.state === HubConnectionState.Connected) return;
    if (url !== this.url) throw new TypeError("Construct a transport with the session endpoint.");
    await this.connection.start();
  }

  on(methodName, handler) {
    if (!this.connection) throw new Error("Start the transport before registering handlers.");
    this.connection.on(methodName, handler);
  }

  onClosed(handler) {
    if (!this.connection) throw new Error("Start the transport before registering handlers.");
    this.connection.onclose(handler);
  }

  async invoke(methodName, ...args) {
    if (!this.connection || this.connection.state !== HubConnectionState.Connected)
      throw new Error("The session transport is not connected.");
    return this.connection.invoke(methodName, ...args);
  }

  async stop() {
    if (!this.connection) return;
    if (this.connection.state !== HubConnectionState.Disconnected)
      await this.connection.stop();
  }

  async dispose() {
    await this.stop();
  }
}

export function createSignalRTransport(url, options) {
  return new SignalRTransport(url, options);
}
