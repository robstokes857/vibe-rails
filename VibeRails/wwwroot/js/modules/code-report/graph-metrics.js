export const reportPath = path => String(path || '').replace(/\\/g, '/').replace(/:\d+(?::\d+)?$/, '').replace(/^\.\//, '');

/** Overlay a validated scan's file measurements without changing the cached working-tree graph. */
export function withReportMetrics(graph, files, startedUtc) {
    const byPath = new Map(files.filter(file => file?.file).map(file => [reportPath(file.file), file]));
    const captured = startedUtc ? new Date(startedUtc) : null;
    const source = captured && Number.isFinite(captured.valueOf())
        ? `Measurements from the scan captured ${captured.toLocaleString()}.`
        : 'Measurements from the saved scan.';
    return {
        ...graph,
        nodes: graph.nodes.map(node => {
            // File-wide values must never be presented as measurements of a directory or symbol.
            const file = node.kind === 'file' ? byPath.get(reportPath(node.path)) : null;
            if (!file) return node;
            const measured = new Map((Array.isArray(file.categories) ? file.categories : [])
                .flatMap(category => Array.isArray(category?.metrics) ? category.metrics : [])
                .filter(metric => Number.isFinite(metric?.value) && metric.value >= 0)
                .map(metric => [metric.name, metric.value]));
            const metrics = {};
            for (const [name, key] of [['lines_of_code', 'loc'], ['cyclomatic_complexity', 'complexity']]) {
                if (measured.has(name)) metrics[key] = measured.get(name);
            }
            if (!Object.keys(metrics).length) return node;
            const complexity = metrics.complexity === undefined ? ''
                : 'Complexity is the highest function cyclomatic complexity in this file.';
            return { ...node, metrics, summary: [node.summary, source, complexity].filter(Boolean).join(' ') };
        })
    };
}
