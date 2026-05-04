window.memoryGraph = (() => {
    let cy = null;

    function destroy() {
        if (cy) { try { cy.destroy(); } catch { } cy = null; }
    }

    function render(containerId, nodes, edges) {
        const container = document.getElementById(containerId);
        if (!container) return;
        destroy();

        const elements = [
            ...nodes.map(n => ({
                data: {
                    id: n.id,
                    label: n.label,
                    kind: n.kind,
                },
            })),
            ...edges.map(e => ({
                data: {
                    id: e.id,
                    source: e.source,
                    target: e.target,
                    relation: e.relation,
                    invalidated: e.invalidated,
                },
            })),
        ];

        cy = cytoscape({
            container,
            elements,
            wheelSensitivity: 0.2,
            style: [
                {
                    selector: 'node',
                    style: {
                        'background-color': '#4a90e2',
                        'label': 'data(label)',
                        'color': '#fff',
                        'text-valign': 'center',
                        'text-halign': 'center',
                        'text-outline-color': '#1f2d3d',
                        'text-outline-width': 2,
                        'font-size': 11,
                        'width': 36,
                        'height': 36,
                    },
                },
                {
                    selector: 'node[kind = "person"]',
                    style: { 'background-color': '#e94e77' },
                },
                {
                    selector: 'node[kind = "organization"]',
                    style: { 'background-color': '#f5a623' },
                },
                {
                    selector: 'node[kind = "project"]',
                    style: { 'background-color': '#7ed321' },
                },
                {
                    selector: 'node[kind = "place"]',
                    style: { 'background-color': '#9013fe' },
                },
                {
                    selector: 'edge',
                    style: {
                        'curve-style': 'bezier',
                        'target-arrow-shape': 'triangle',
                        'line-color': '#94a3b8',
                        'target-arrow-color': '#94a3b8',
                        'label': 'data(relation)',
                        'font-size': 9,
                        'color': '#475569',
                        'text-background-color': '#fff',
                        'text-background-opacity': 0.85,
                        'text-background-padding': 2,
                        'width': 1.5,
                    },
                },
                {
                    selector: 'edge[?invalidated]',
                    style: {
                        'line-style': 'dashed',
                        'line-color': '#cbd5e1',
                        'target-arrow-color': '#cbd5e1',
                        'opacity': 0.6,
                    },
                },
            ],
            layout: {
                name: 'cose',
                animate: false,
                padding: 30,
                nodeRepulsion: 6000,
                idealEdgeLength: 90,
            },
        });
    }

    function fit() {
        if (cy) cy.fit(undefined, 30);
    }

    return { render, fit, destroy };
})();
