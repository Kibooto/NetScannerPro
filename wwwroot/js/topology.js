window.drawNetworkTopology = (containerId, nodesData, edgesData) => {
    const container = document.getElementById(containerId);
    if (!container) {
        return;
    }

    const nodes = new vis.DataSet(nodesData || []);
    const edges = new vis.DataSet(edgesData || []);

    const options = {
        autoResize: true,
        nodes: {
            shape: "dot",
            size: 18,
            font: {
                color: "#e5e7eb",
                size: 14
            },
            borderWidth: 2
        },
        edges: {
            color: {
                color: "#4b5563",
                highlight: "#93c5fd"
            },
            smooth: {
                enabled: true,
                type: "dynamic"
            }
        },
        physics: {
            enabled: true,
            solver: "forceAtlas2Based",
            stabilization: {
                enabled: false
            },
            forceAtlas2Based: {
                gravitationalConstant: -80,
                centralGravity: 0.015,
                springLength: 120,
                springConstant: 0.06,
                damping: 0.55
            }
        },
        interaction: {
            hover: true,
            zoomView: true,
            dragView: true
        }
    };

    new vis.Network(container, { nodes, edges }, options);
};
