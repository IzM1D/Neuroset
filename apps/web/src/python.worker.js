/* Runs only inside an opaque-origin sandbox iframe. Never receives application settings. */
const runtimeRoot = 'https://cdn.jsdelivr.net/pyodide/v0.27.7/full/';
let output = '';
const append = text => { output = (output + text + '\n').slice(0, 20000); };

self.onmessage = async ({ data }) => {
  try {
    importScripts(runtimeRoot + 'pyodide.js');
    const pyodide = await loadPyodide({ indexURL: runtimeRoot, stdout: append, stderr: append });
    await pyodide.loadPackagesFromImports(data.code);
    await pyodide.runPythonAsync(`
import os
os.environ['MPLBACKEND'] = 'Agg'
`);
    let failure = '';
    try {
      const result = await pyodide.runPythonAsync(data.code);
      if (result !== undefined && result !== null) append(String(result));
      result?.destroy?.();
    } catch (error) { failure = String(error).slice(0, 6000); }
    const plots = await pyodide.runPythonAsync(`
import sys, json, io, base64
_neuroset_plots = []
if 'matplotlib.pyplot' in sys.modules:
    import matplotlib.pyplot as plt
    for number in plt.get_fignums()[:4]:
        buffer = io.BytesIO()
        plt.figure(number).savefig(buffer, format='png', dpi=110, bbox_inches='tight')
        if buffer.tell() <= 8 * 1024 * 1024:
            _neuroset_plots.append(base64.b64encode(buffer.getvalue()).decode('ascii'))
    plt.close('all')
json.dumps(_neuroset_plots)
`);
    self.postMessage({ output, error: failure, plots: JSON.parse(plots) });
  } catch (error) { self.postMessage({ output, error: String(error).slice(0, 6000), plots: [] }); }
};
