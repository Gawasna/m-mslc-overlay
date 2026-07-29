import { EditorState, Plugin, PluginKey } from "prosemirror-state";
import { EditorView, Decoration, DecorationSet } from "prosemirror-view";
import { Schema, DOMParser, DOMSerializer } from "prosemirror-model";
import { history, undo, redo } from "prosemirror-history";
import { keymap } from "prosemirror-keymap";
import { baseKeymap } from "prosemirror-commands";

// 1. Define Schema
const schema = new Schema({
  nodes: {
    doc: { content: "(freeform_block | machine_segment | chunk_break)+" },

    machine_segment: {
      attrs: {
        segId: { default: null },
        tsStartMs: { default: 0 },
        tsEndMs: { default: 0 },
        speakerId: { default: "UNK" },
        commitType: { default: "HARD" }
      },
      content: "seg_text seg_trs?",
      marks: "",
      group: "block",
      draggable: false,
      toDOM(node) { return ["div", { class: "machine-segment", "data-seg-id": node.attrs.segId }, 0]; }
    },

    seg_text: {
      content: "text*",
      marks: "",
      toDOM() { return ["div", { class: "seg-text" }, 0]; }
    },

    seg_trs: {
      content: "text*",
      marks: "",
      toDOM() { return ["div", { class: "seg-trs" }, 0]; }
    },

    freeform_block: {
      attrs: {
        blockId: { default: null },
        anchorAfter: { default: null },
      },
      content: "inline*",
      marks: "strong em",
      group: "block",
      toDOM(node) { return ["div", { class: "freeform-block", "data-block-id": node.attrs.blockId }, 0]; }
    },

    chunk_break: {
      attrs: { chunkId: { default: null } },
      group: "block",
      isLeaf: true,
      toDOM(node) { return ["hr", { class: "chunk-break", "data-chunk-id": node.attrs.chunkId }]; }
    },

    text: { group: "inline" },
    hard_break: {
      inline: true,
      group: "inline",
      selectable: false,
      parseDOM: [{ tag: "br" }],
      toDOM() { return ["br"]; }
    },
  },
  marks: {
    strong: {
      parseDOM: [{ tag: "strong" }, { tag: "b" }],
      toDOM() { return ["strong", 0] }
    },
    em: {
      parseDOM: [{ tag: "i" }, { tag: "em" }],
      toDOM() { return ["em", 0] }
    },
  }
});

function formatTimestamp(ms) {
    const totalSeconds = Math.floor(ms / 1000);
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;
    return `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
}

function sendToHost(msg) {
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(JSON.stringify(msg));
    } else {
        console.log("SEND TO HOST:", msg);
    }
}

class MachineSegmentView {
  constructor(node, view, getPos) {
    this.dom = document.createElement("div");
    this.dom.className = "machine-segment";
    this.dom.setAttribute("data-seg-id", node.attrs.segId);
    // Important: we don't want ProseMirror to treat this as directly editable text
    this.dom.contentEditable = "false";

    const gutter = document.createElement("span");
    gutter.className = "seg-gutter";
    gutter.textContent = formatTimestamp(node.attrs.tsStartMs) + " [" + node.attrs.speakerId + "]";
    gutter.addEventListener("click", () => sendToHost({ type: "PLAY_AUDIO", segId: node.attrs.segId }));

    const content = document.createElement("div");
    content.className = "seg-content";
    
    // We let ProseMirror manage the content via contentDOM
    this.contentDOM = content;

    this.dom.append(gutter, content);

    this.dom.addEventListener("dblclick", () => {
      sendToHost({ type: "OPEN_EDIT_FIELD", segId: node.attrs.segId, pos: getPos() });
    });
  }
  
  // Ignore mutation events because it's managed externally or read-only
  ignoreMutation() { return true; }
  stopEvent(e) { 
      // Allow double clicks and clicks to pass through
      if (e.type === 'dblclick' || e.type === 'click') return false;
      return true; 
  }
}

class ChunkBreakView {
    constructor(node) {
        this.dom = document.createElement("div");
        this.dom.className = "chunk-break";
        this.dom.contentEditable = "false";
        this.dom.textContent = "CHUNK " + node.attrs.chunkId;
    }
    stopEvent() { return true; }
}

class FreeformBlockView {
    constructor(node) {
        this.dom = document.createElement("p");
        this.dom.className = "freeform-block";
        if (node.attrs.blockId) {
            this.dom.setAttribute("data-block-id", node.attrs.blockId);
        }
        if (node.attrs.anchorAfter) {
            this.dom.setAttribute("data-anchor", node.attrs.anchorAfter);
        }
        this.contentDOM = this.dom;
    }
}

// 2. Plugins
const magicCursorPluginKey = new PluginKey("magicCursor");
let magicCursorPos = null;

const magicCursorPlugin = new Plugin({
  key: magicCursorPluginKey,
  state: {
    init: () => DecorationSet.empty,
    apply(tr, oldSet) {
      const meta = tr.getMeta(magicCursorPluginKey);
      if (meta !== undefined) return meta;
      // Map decorations across changes
      return oldSet.map(tr.mapping, tr.doc);
    }
  },
  props: {
    decorations(state) {
      return this.getState(state);
    }
  }
});

const scrollModePluginKey = new PluginKey("scrollMode");
const scrollModePlugin = new Plugin({
  key: scrollModePluginKey,
  state: {
    init: () => ({ mode: "FREE_INPUT" }),
    apply(tr, prev) {
      const meta = tr.getMeta(scrollModePluginKey);
      return meta ? meta : prev;
    }
  },
  view(editorView) {
    return {
      update(view, prevState) {
        const { mode } = scrollModePluginKey.getState(view.state);
        if (mode === "WATCH_MAGIC" && magicCursorPos !== null) {
          // Scroll to magic cursor
          const coords = view.coordsAtPos(magicCursorPos);
          window.scrollTo({ top: coords.top + window.scrollY - 200, behavior: "smooth" });
        }
      }
    };
  }
});

// Notify host of changes in freeform blocks and caret position
const trackChangesPlugin = new Plugin({
    view(editorView) {
        return {
            update(view, prevState) {
                if (!view.state.selection.eq(prevState.selection)) {
                    sendToHost({ 
                        type: "JS_DEBUG", 
                        message: "Caret pos: " + view.state.selection.from + ", Magic cursor pos: " + magicCursorPos 
                    });
                }
                
                if (view.state.doc.eq(prevState.doc)) return;
                
                // Diff and find changed freeform blocks
                // For simplicity, we just send all freeform blocks that have text content
                if (window.changeTimeout) clearTimeout(window.changeTimeout);
                window.changeTimeout = setTimeout(() => {
                    view.state.doc.descendants((node, pos) => {
                        if (node.type.name === "freeform_block") {
                            sendToHost({ 
                                type: "FREEFORM_CHANGED",
                                blockId: node.attrs.blockId,
                                anchorAfter: node.attrs.anchorAfter,
                                content: node.textContent
                            });
                        }
                    });
                }, 1000);
            }
        }
    }
});

// 3. Editor Initialization
let view;

const insertBreak = (state, dispatch) => {
    let { $from } = state.selection;
    if ($from.parent.type.name === "freeform_block") {
        if (dispatch) dispatch(state.tr.replaceSelectionWith(schema.nodes.hard_break.create()).scrollIntoView());
        return true;
    }
    return false;
};

function initEditor() {
    view = new EditorView(document.getElementById("editor"), {
        state: EditorState.create({
            schema,
            plugins: [
                history(),
                keymap({ "Mod-z": undo, "Mod-y": redo, "Enter": insertBreak, "Shift-Enter": insertBreak }),
                keymap(baseKeymap),
                magicCursorPlugin,
                scrollModePlugin,
                trackChangesPlugin
            ]
        }),
        nodeViews: {
            machine_segment(node, view, getPos) { return new MachineSegmentView(node, view, getPos); },
            chunk_break(node) { return new ChunkBreakView(node); },
            freeform_block(node) { return new FreeformBlockView(node); }
        }
    });

    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', event => {
            try {
                sendToHost({ type: "JS_ERROR", message: "DEBUG JS RECEIVED: " + typeof event.data });
                if (window.__bridge && window.__bridge.receive) {
                    let msg = event.data;
                    if (typeof msg === 'string') {
                        msg = JSON.parse(msg);
                    }
                    window.__bridge.receive(msg);
                }
            } catch(ex) {
                sendToHost({ type: "JS_ERROR", message: "DEBUG JS LISTENER ERROR: " + ex.toString() });
            }
        });
    }

    sendToHost({ type: "DOCUMENT_READY" });
}

// 4. Bridge Functions
window.__bridge = {
    receive(msg) {
        if (!view) return;
        
        try {
            if (msg.type === "LOAD_DOCUMENT") {
                // Build doc
                const nodes = [];
            
            // Helper to get freeform content for an anchor
            const getFreeform = (anchorId) => {
                if (!msg.freeformBlocks) return null;
                return msg.freeformBlocks.find(b => b.anchorAfter === anchorId);
            };

            const createFreeformNode = (anchorId) => {
                const b = getFreeform(anchorId);
                const attrs = { anchorAfter: anchorId, blockId: b ? b.blockId : null };
                const contentNodes = b && b.content ? [schema.text(b.content)] : [];
                return schema.nodes.freeform_block.create(attrs, contentNodes);
            };
            
            if (!msg.segments || msg.segments.length === 0) {
                nodes.push(createFreeformNode(null));
            } else {
                nodes.push(createFreeformNode(null)); // Always add the first freeform block!
                for (const seg of msg.segments) {
                    const textNode = schema.nodes.seg_text.create({}, schema.text(seg.textSrc));
                    const segNode = schema.nodes.machine_segment.create(
                        { segId: seg.segId, tsStartMs: seg.tsStartMs, tsEndMs: seg.tsEndMs, speakerId: seg.speakerId },
                        textNode
                    );
                    nodes.push(segNode);
                    
                    // Add freeform block after each segment
                    nodes.push(createFreeformNode(seg.segId));
                }
            }
            
            const doc = schema.nodes.doc.create({}, nodes);
            const state = EditorState.create({
                doc,
                plugins: view.state.plugins
            });
            view.updateState(state);
            
        } else if (msg.type === "INSERT_MACHINE_SEGMENT") {
            const { segId, tsStartMs, tsEndMs, speakerId, textSrc, textTrs } = msg;
            
            // Build node
            // Build node
            const textNode = schema.nodes.seg_text.create({}, schema.text(textSrc));
            let contentNodes = [textNode];
            if (textTrs) {
                contentNodes.push(schema.nodes.seg_trs.create({}, schema.text(textTrs)));
            }
            
            const node = schema.nodes.machine_segment.create(
                { segId, tsStartMs, tsEndMs, speakerId },
                contentNodes
            );
            
            // Also append a freeform block right after it
            const freeform = schema.nodes.freeform_block.create({ anchorAfter: segId });
            
            let insertPos = magicCursorPos !== null ? magicCursorPos : view.state.doc.content.size;
            
            const tr = view.state.tr.insert(insertPos, [node, freeform]);
            
            // Advance cursor
            magicCursorPos = insertPos + node.nodeSize + freeform.nodeSize;
            
            // Update decoration
            const deco = DecorationSet.create(tr.doc, [
                Decoration.widget(magicCursorPos, () => {
                    const el = document.createElement("span");
                    el.className = "magic-cursor-indicator";
                    return el;
                }, { side: -1 })
            ]);
            tr.setMeta(magicCursorPluginKey, deco);
            
            view.dispatch(tr);
            
        } else if (msg.type === "SET_MAGIC_CURSOR") {
            magicCursorPos = msg.pos;
            const deco = DecorationSet.create(view.state.doc, [
                Decoration.widget(magicCursorPos, () => {
                    const el = document.createElement("span");
                    el.className = "magic-cursor-indicator";
                    return el;
                }, { side: -1 })
            ]);
            view.dispatch(view.state.tr.setMeta(magicCursorPluginKey, deco));
            
        } else if (msg.type === "SET_SCROLL_MODE") {
            view.dispatch(view.state.tr.setMeta(scrollModePluginKey, { mode: msg.mode }));
            
        } else if (msg.type === "APPLY_PATCH") {
            // Find node by segId
            let pos = -1;
            view.state.doc.descendants((node, p) => {
                if (node.type.name === "machine_segment" && node.attrs.segId === msg.segId) {
                    pos = p;
                    return false;
                }
            });
            if (pos !== -1) {
                const node = view.state.doc.nodeAt(pos);
                // Simple replacement of the segment text
                const textNode = schema.nodes.seg_text.create({}, schema.text(msg.newValue));
                const newNode = schema.nodes.machine_segment.create(node.attrs, textNode);
                
                view.dispatch(view.state.tr.replaceWith(pos, pos + node.nodeSize, newNode));
            }
            }
        } catch (e) {
            sendToHost({ type: "JS_ERROR", message: e.toString(), stack: e.stack });
        }
    }
};

window.simulateMagicCursor = function(ts, spk, text) {
    window.__bridge.receive({
        type: "INSERT_MACHINE_SEGMENT",
        segId: "sim_" + Date.now(),
        tsStartMs: 0,
        tsEndMs: 0,
        speakerId: spk,
        textSrc: text
    });
};

if (document.readyState === 'loading') {
    document.addEventListener("DOMContentLoaded", initEditor);
} else {
    initEditor();
}
