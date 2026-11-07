import { atlasQuality } from './findings.js';

export const reportPath = path => String(path || '').replace(/\\/g, '/').replace(/:\d+(?::\d+)?$/, '').replace(/^\.\//, '');

/**
 * Overlay a validated scan's file results without changing the cached working-tree graph: the raw
 * size and complexity measurements, the inspector's problem summary (`quality`) and one line of
 * provenance. A file the scan did not cover says so, so an empty inspector never reads as clean.
 */
export function withReportMetrics(graph, files, startedUtc) {
    const byPath = new Map(files.filter(file => file?.file).map(file => [reportPath(file.file), file]));
    const captured = startedUtc ? new Date(startedUtc) : null;
    const source = captured && Number.isFinite(captured.valueOf())
        ? `Measured in the scan from ${captured.toLocaleString()}.`
        : 'Measured in the saved scan.';
    return {
        ...graph,
        nodes: graph.nodes.map(node => {
            // File-wide values must never be presented as measurements of a directory or symbol.
            if (node.kind !== 'file') return node;
            const file = byPath.get(reportPath(node.path));
            if (!file) return byPath.size ? { ...node, summary: 'Not in this scan.' } : node;
            const measured = new Map((Array.isArray(file.categories) ? file.categories : [])
                .flatMap(category => Array.isArray(category?.metrics) ? category.metrics : [])
                .filter(metric => Number.isFinite(metric?.value) && metric.value >= 0)
                .map(metric => [metric.name, metric.value]));
            const metrics = {};
            for (const [name, key] of [['lines_of_code', 'loc'], ['cyclomatic_complexity', 'complexity']]) {
                if (measured.has(name)) metrics[key] = measured.get(name);
            }
            const quality = atlasQuality(file);
            if (!Object.keys(metrics).length && !quality) return node;
            const result = { ...node, summary: source };
            if (Object.keys(metrics).length) result.metrics = metrics;
            if (quality) result.quality = quality;
            return result;
        })
    };
}
