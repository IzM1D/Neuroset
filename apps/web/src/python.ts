import workerSource from './python.worker.js?raw';

export interface PythonResult { output: string; error: string; plots: string[] }

export function runPython(code: string, signal: AbortSignal, timeout = 120_000): Promise<PythonResult> {
  signal.throwIfAborted();
  if (!code.trim() || code.length > 24000) return Promise.reject(new Error('Python-код пустой или превышает 24000 символов.'));
  return new Promise((resolve, reject) => {
    const iframe = document.createElement('iframe');
    iframe.hidden = true;
    iframe.setAttribute('sandbox', 'allow-scripts');
    iframe.setAttribute('referrerpolicy', 'no-referrer');
    const channel = new MessageChannel();
    let timer: ReturnType<typeof setTimeout>;
    const cleanup = () => {
      clearTimeout(timer);
      signal.removeEventListener('abort', abort);
      channel.port1.close();
      iframe.remove(); // Destroy the iframe and its worker, including infinite Python loops.
    };
    const fail = (reason: unknown) => { cleanup(); reject(reason); };
    const abort = () => fail(signal.reason);
    signal.addEventListener('abort', abort, { once: true });
    timer = setTimeout(() => fail(new Error('Python: превышено время выполнения (2 минуты).')), timeout);
    channel.port1.onmessage = ({ data }) => {
      if (!data || typeof data.output !== 'string' || typeof data.error !== 'string' || !Array.isArray(data.plots)) {
        fail(new Error('Python вернул некорректный результат.')); return;
      }
      const result = { output: data.output.slice(0, 20000), error: data.error.slice(0, 6000),
        plots: data.plots.filter((plot: unknown) => typeof plot === 'string' && plot.length <= 12_000_000).slice(0, 4) };
      cleanup();
      resolve(result);
    };
    const source = JSON.stringify(workerSource).replace(/</g, '\\u003c');
    iframe.srcdoc = `<!doctype html><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval' blob: https://cdn.jsdelivr.net; worker-src blob:; connect-src https://cdn.jsdelivr.net; img-src 'none'; frame-src 'none'; form-action 'none'"><script>
      addEventListener('message', event => {
        if (event.source !== parent || !event.ports[0]) return;
        const port = event.ports[0];
        const worker = new Worker(URL.createObjectURL(new Blob([${source}], {type: 'text/javascript'})));
        worker.onmessage = event => { port.postMessage(event.data); worker.terminate(); };
        worker.onerror = () => { port.postMessage({output:'', error:'Не удалось загрузить Python. Проверьте доступ к cdn.jsdelivr.net.', plots:[]}); worker.terminate(); };
        worker.postMessage({code: event.data.code});
      }, {once:true});
    <\/script>`;
    iframe.onload = () => iframe.contentWindow?.postMessage({ code }, '*', [channel.port2]);
    document.body.append(iframe);
  });
}
