// Hands the microphone's samples back to the page, and nothing else. A file rather than a Blob URL,
// because the page's Content-Security-Policy allows scripts from this origin only.
class Tap extends AudioWorkletProcessor {
  process(inputs) {
    const channel = inputs[0] && inputs[0][0];
    if (channel && channel.length) this.port.postMessage(channel.slice(0));
    return true;
  }
}
registerProcessor("tap", Tap);
