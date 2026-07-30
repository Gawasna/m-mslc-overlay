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

    update(node) {
        if (node.type.name !== "freeform_block") return false;
        if (node.attrs.blockId) {
            this.dom.setAttribute("data-block-id", node.attrs.blockId);
        } else {
            this.dom.removeAttribute("data-block-id");
        }
        if (node.attrs.anchorAfter) {
            this.dom.setAttribute("data-anchor", node.attrs.anchorAfter);
        } else {
            this.dom.removeAttribute("data-anchor");
        }
        return true;
    }
}

const ICONS = {
    "FREE_INPUT": `<svg viewBox="0 0 24 24" width="24" height="24"><path fill="currentColor" d="M14.06,9L15,9.94L5.92,19H5V18.08L14.06,9M17.66,3C17.41,3 17.15,3.1 16.96,3.29L15.13,5.12L18.88,8.87L20.71,7.04C21.1,6.65 21.1,6 20.71,5.63L18.37,3.29C18.17,3.09 17.92,3 17.66,3M14.06,6.19L3,17.25V21H6.75L17.81,9.94L14.06,6.19Z" /></svg>`,
    "WATCH_MAGIC": `<svg viewBox="0 0 24 24" width="24" height="24"><path fill="currentColor" d="M16 13H13V16H11V13H8V11H11V8H13V11H16V13M12 2C17.5 2 22 6.5 22 12C22 17.5 17.5 22 12 22C6.5 22 2 17.5 2 12C2 6.5 6.5 2 12 2M12 4C7.58 4 4 7.58 4 12C4 16.42 7.58 20 12 20C16.42 20 20 16.42 20 12C20 7.58 16.42 4 12 4Z" /></svg>`,
    "DONOTHING": `<svg viewBox="0 0 24 24" width="24" height="24"><path fill="currentColor" d="M12 2C17.5 2 22 6.5 22 12C22 17.5 17.5 22 12 22C6.5 22 2 17.5 2 12C2 6.5 6.5 2 12 2M12 4C7.58 4 4 7.58 4 12C4 16.42 7.58 20 12 20C16.42 20 20 16.42 20 12C20 7.58 16.42 4 12 4M11 16V18H13V16H11M12 6C9.79 6 8 7.79 8 10H10C10 8.9 10.9 8 12 8C13.1 8 14 8.9 14 10C14 11.5 11 11.25 11 14H13C13 12.25 16 12 16 10C16 7.79 14.21 6 12 6Z" /></svg>`
};

const TOOLTIPS = {
    "FREE_INPUT": "Free Input Mode (Auto-scroll Off)",
    "WATCH_MAGIC": "Watch Magic Mode (Auto-scroll On)",
    "DONOTHING": "Read Mode (Locked)"
};

// 2. Plugins
const magicCursorPluginKey = new PluginKey("magicCursor");
let magicCursorPos = null;

const magicCursorPlugin = new Plugin({
  key: magicCursorPluginKey,
  state: {
    init: () => DecorationSet.empty,
      apply(tr, oldSet) {
        const meta = tr.getMeta(magicCursorPluginKey);
        if (meta && meta.newPos !== undefined) {
            magicCursorPos = meta.newPos;
            return meta.deco || oldSet;
        }
        
        if (tr.docChanged && magicCursorPos !== null) {
            magicCursorPos = tr.mapping.map(magicCursorPos);
        }
        
        if (meta && meta.deco !== undefined) return meta.deco;
        
        // Fallback for old meta format if any
        if (meta !== undefined && !meta.newPos && !meta.deco) return meta;

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
        if (tr.docChanged && !tr.getMeta(magicCursorPluginKey) && !tr.getMeta(scrollModePluginKey)) {
          if (prev.mode === "WATCH_MAGIC") {
            sendToHost({ type: "SCROLL_MODE_CHANGED", mode: "FREE_INPUT" });
            return { mode: "FREE_INPUT" };
          }
        }
        const meta = tr.getMeta(scrollModePluginKey);
        return meta ? meta : prev;
      }
  },
  view(editorView) {
    return {
      update(view, prevState) {
        const { mode } = scrollModePluginKey.getState(view.state);
        const prevMode = scrollModePluginKey.getState(prevState).mode;
        
        if (mode !== prevMode) {
            let indicator = document.getElementById("scroll-indicator");
            if (indicator) {
                indicator.innerHTML = ICONS[mode] || ICONS["FREE_INPUT"];
                indicator.title = TOOLTIPS[mode] || TOOLTIPS["FREE_INPUT"];
            }
        }
        
        let modeSwitchedToWatch = mode === "WATCH_MAGIC" && prevMode !== "WATCH_MAGIC";
        if (mode === "WATCH_MAGIC" && magicCursorPos !== null && (window.forceScrollMagic || modeSwitchedToWatch)) {
          const coords = view.coordsAtPos(magicCursorPos);
          window.scrollTo({ top: coords.top + window.scrollY - 200, behavior: "smooth" });
          window.forceScrollMagic = false;
        }
      }
    };
  }
});

const trackChangesPluginKey = new PluginKey("trackChanges");
const trackChangesPlugin = new Plugin({
    key: trackChangesPluginKey,
    state: {
        init() { return { dirtyPositions: [] }; },
          apply(tr, prev) {
              if (!tr.docChanged) return prev;
              let dirty = [];
              for (let i = 0; i < tr.steps.length; i++) {
                  let stepMap = tr.steps[i].getMap();
                  let mapped = tr.mapping.slice(i + 1);
                  stepMap.forEach((oldStart, oldEnd, newStart, newEnd) => {
                      dirty.push(mapped.map(newStart));
                  });
              }
              return { dirtyPositions: dirty };
          }
    },
    view(editorView) {
        return {
            update(view, prevState) {
                if (view.state.doc.eq(prevState.doc)) return;
                
                if (window.changeTimeout) clearTimeout(window.changeTimeout);
                window.changeTimeout = setTimeout(() => {
                    const state = trackChangesPluginKey.getState(view.state);
                    if (!state || state.dirtyPositions.length === 0) return;
                    
                    let dirtyBlocks = new Map();
                    state.dirtyPositions.forEach(pos => {
                        let actualPos = Math.min(pos, view.state.doc.content.size);
                        view.state.doc.nodesBetween(actualPos, actualPos, (node, nPos) => {
                            if (node.type.name === "freeform_block") {
                                const key = node.attrs.blockId || node.attrs.anchorAfter || '__root__';
                                dirtyBlocks.set(key, {
                                    blockId: node.attrs.blockId,
                                    anchorAfter: node.attrs.anchorAfter,
                                    content: node.textContent
                                });
                            }
                            return false;
                        });
                    });
                    
                    dirtyBlocks.forEach(b => {
                        sendToHost({ 
                            type: "FREEFORM_CHANGED",
                            blockId: b.blockId,
                            anchorAfter: b.anchorAfter,
                            content: b.content
                        });
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
    let scrollIndicator = document.createElement("div");
    scrollIndicator.id = "scroll-indicator";
    scrollIndicator.innerHTML = ICONS["FREE_INPUT"];
    scrollIndicator.title = TOOLTIPS["FREE_INPUT"];
    document.body.appendChild(scrollIndicator);
    
    scrollIndicator.addEventListener("click", () => {
        let current = scrollModePluginKey.getState(view.state).mode;
        let next = "FREE_INPUT";
        if (current === "FREE_INPUT") next = "WATCH_MAGIC";
        else if (current === "WATCH_MAGIC") next = "DONOTHING";
        
        sendToHost({ type: "SCROLL_MODE_CHANGED", mode: next });
        view.dispatch(view.state.tr.setMeta(scrollModePluginKey, { mode: next }));
    });

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

    document.getElementById("editor").addEventListener("contextmenu", (e) => {
        e.preventDefault();
        const coords = { left: e.clientX, top: e.clientY };
        const pos = view.posAtCoords(coords);
        if (pos) {
            let targetId = null;
            let menuType = "Unknown";
            const $pos = view.state.doc.resolve(pos.pos);
            for (let d = $pos.depth; d > 0; d--) {
                const node = $pos.node(d);
                if (node.type.name === "machine_segment") {
                    menuType = "MachineSegment";
                    targetId = node.attrs.segId;
                    break;
                } else if (node.type.name === "freeform_block") {
                    menuType = "FreeformBlock";
                    targetId = node.attrs.blockId || node.attrs.anchorAfter;
                    break;
                }
            }
            sendToHost({
                type: "SHOW_CONTEXT_MENU",
                menuType: menuType,
                targetId: targetId
            });
        }
    });

    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', event => {
            try {
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
                const nodes = [];
            
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
                    nodes.push(createFreeformNode(null));
                    for (const seg of msg.segments) {
                        const textNode = schema.nodes.seg_text.create({}, schema.text(seg.textSrc));
                        const segNode = schema.nodes.machine_segment.create(
                            { segId: seg.segId, tsStartMs: seg.tsStartMs, tsEndMs: seg.tsEndMs, speakerId: seg.speakerId },
                            textNode
                        );
                        nodes.push(segNode);
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
            
            const textNode = schema.nodes.seg_text.create({}, schema.text(textSrc));
            let contentNodes = [textNode];
            if (textTrs) {
                contentNodes.push(schema.nodes.seg_trs.create({}, schema.text(textTrs)));
            }
            
            const node = schema.nodes.machine_segment.create(
                { segId, tsStartMs, tsEndMs, speakerId },
                contentNodes
            );
            
            const freeform = schema.nodes.freeform_block.create({ anchorAfter: segId });
            
            let insertPos = magicCursorPos !== null ? magicCursorPos : view.state.doc.content.size;
            
            const tr = view.state.tr.insert(insertPos, [node, freeform]);
            
            let newMagicCursorPos = insertPos + node.nodeSize + freeform.nodeSize;
            
            const deco = DecorationSet.create(tr.doc, [
                Decoration.widget(newMagicCursorPos, () => {
                    const el = document.createElement("span");
                    el.className = "magic-cursor-indicator";
                    return el;
                }, { side: -1 })
            ]);
            tr.setMeta(magicCursorPluginKey, { deco: deco, newPos: newMagicCursorPos });
            
            window.forceScrollMagic = true;
            view.dispatch(tr);
            
        } else if (msg.type === "SET_MAGIC_CURSOR") {
            const deco = DecorationSet.create(view.state.doc, [
                Decoration.widget(msg.pos, () => {
                    const el = document.createElement("span");
                    el.className = "magic-cursor-indicator";
                    return el;
                }, { side: -1 })
            ]);
            window.forceScrollMagic = true;
            view.dispatch(view.state.tr.setMeta(magicCursorPluginKey, { deco: deco, newPos: msg.pos }));
            
        } else if (msg.type === "SET_SCROLL_MODE") {
            view.dispatch(view.state.tr.setMeta(scrollModePluginKey, { mode: msg.mode }));
            
        } else if (msg.type === "APPLY_PATCH") {
            let pos = -1;
            let existingNode = null;
            view.state.doc.descendants((node, p) => {
                if (node.type.name === "machine_segment" && node.attrs.segId === msg.segId) {
                    pos = p;
                    existingNode = node;
                    return false;
                }
            });
            if (pos !== -1 && existingNode) {
                let contentNodes = [];
                let existingTextSrcNode = existingNode.child(0);
                let existingTextTrsNode = existingNode.childCount > 1 ? existingNode.child(1) : null;

                if (msg.field === "TextSrc") {
                    contentNodes.push(schema.nodes.seg_text.create({}, schema.text(msg.newValue)));
                    if (existingTextTrsNode) {
                        contentNodes.push(existingTextTrsNode);
                    }
                } else if (msg.field === "TextTrs") {
                    contentNodes.push(existingTextSrcNode);
                    if (msg.newValue) {
                        contentNodes.push(schema.nodes.seg_trs.create({}, schema.text(msg.newValue)));
                    }
                } else {
                    return;
                }

                const newNode = schema.nodes.machine_segment.create(existingNode.attrs, contentNodes);
                view.dispatch(view.state.tr.replaceWith(pos, pos + existingNode.nodeSize, newNode));
            }
        } else if (msg.type === "FREEFORM_PERSISTED") {
            const { anchorAfter, blockId } = msg;
            let found = false;

            view.state.doc.descendants((node, pos) => {
                if (found) return false;
                if (node.type.name === "freeform_block" && node.attrs.anchorAfter === anchorAfter) {
                    view.dispatch(
                        view.state.tr.setNodeMarkup(pos, null, {
                            ...node.attrs,
                            blockId: blockId
                        })
                    );
                    found = true;
                    return false;
                }
            });
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
