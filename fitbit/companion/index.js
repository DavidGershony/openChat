import * as messaging from "messaging";
import { settingsStorage } from "settings";

// Bridge connection config
const BRIDGE_URL = "http://127.0.0.1:18457/api/v1";
const POLL_INTERVAL_MS = 5000;

let pollTimer = null;
let isConnected = false;

/**
 * Get the stored auth token from settings.
 */
function getToken() {
  const tokenSetting = settingsStorage.getItem("bridge_token");
  return tokenSetting || null;
}

/**
 * Make an authenticated fetch request to the bridge.
 */
async function bridgeFetch(path, options = {}) {
  const token = getToken();
  if (!token) {
    console.log("[companion] No auth token, skipping request");
    return null;
  }

  const headers = {
    "Authorization": `Bearer ${token}`,
    "Content-Type": "application/json",
    ...(options.headers || {})
  };

  try {
    const response = await fetch(`${BRIDGE_URL}${path}`, {
      ...options,
      headers
    });

    if (response.status === 401) {
      console.log("[companion] Token rejected (401), clearing auth");
      settingsStorage.removeItem("bridge_token");
      sendToWatch({ type: "auth_error", message: "Watch unpaired. Re-pair in settings." });
      stopPolling();
      return null;
    }

    return response;
  } catch (err) {
    console.error(`[companion] Bridge fetch failed: ${err.message}`);
    return null;
  }
}

/**
 * Fetch the chat list from the bridge and send to watch.
 */
async function fetchChats() {
  const response = await bridgeFetch("/chats");
  if (!response) return;

  try {
    const data = await response.json();
    sendToWatch({ type: "chat_list", chats: data.chats || [] });
  } catch (err) {
    console.error(`[companion] Failed to parse chats: ${err.message}`);
  }
}

/**
 * Fetch messages for a specific chat and send to watch.
 */
async function fetchMessages(chatId, limit = 20) {
  const response = await bridgeFetch(`/chats/${encodeURIComponent(chatId)}/messages?limit=${limit}`);
  if (!response) return;

  try {
    const data = await response.json();
    sendToWatch({ type: "messages", chatId, messages: data.messages || [] });
  } catch (err) {
    console.error(`[companion] Failed to parse messages: ${err.message}`);
  }
}

/**
 * Send a message via the bridge.
 */
async function sendMessage(chatId, content) {
  const response = await bridgeFetch(`/chats/${encodeURIComponent(chatId)}/messages`, {
    method: "POST",
    body: JSON.stringify({ content })
  });

  if (!response) {
    sendToWatch({ type: "send_result", chatId, success: false });
    return;
  }

  try {
    const data = await response.json();
    sendToWatch({ type: "send_result", chatId, success: true, messageId: data.message_id });
  } catch (err) {
    console.error(`[companion] Failed to parse send result: ${err.message}`);
    sendToWatch({ type: "send_result", chatId, success: false });
  }
}

/**
 * Mark a chat as read via the bridge.
 */
async function markRead(chatId) {
  await bridgeFetch(`/chats/${encodeURIComponent(chatId)}/read`, { method: "POST" });
}

/**
 * Send a message to the watch app via Fitbit Messaging API.
 */
function sendToWatch(data) {
  if (messaging.peerSocket.readyState === messaging.peerSocket.OPEN) {
    try {
      messaging.peerSocket.send(JSON.stringify(data));
    } catch (err) {
      console.error(`[companion] Failed to send to watch: ${err.message}`);
    }
  }
}

/**
 * Handle messages received from the watch app.
 */
messaging.peerSocket.addEventListener("message", (evt) => {
  let msg;
  try {
    msg = typeof evt.data === "string" ? JSON.parse(evt.data) : evt.data;
  } catch (err) {
    console.error(`[companion] Invalid message from watch: ${err.message}`);
    return;
  }

  console.log(`[companion] Watch request: ${msg.type}`);

  switch (msg.type) {
    case "get_chats":
      fetchChats();
      break;
    case "get_messages":
      fetchMessages(msg.chatId, msg.limit || 20);
      break;
    case "send_message":
      sendMessage(msg.chatId, msg.content);
      break;
    case "mark_read":
      markRead(msg.chatId);
      break;
    default:
      console.log(`[companion] Unknown message type: ${msg.type}`);
  }
});

messaging.peerSocket.addEventListener("open", () => {
  console.log("[companion] Watch connected");
  isConnected = true;

  // Send initial chat list when watch connects
  if (getToken()) {
    fetchChats();
  }
});

messaging.peerSocket.addEventListener("close", () => {
  console.log("[companion] Watch disconnected");
  isConnected = false;
});

messaging.peerSocket.addEventListener("error", (err) => {
  console.error(`[companion] Socket error: ${err.message}`);
});

// === Polling for new messages ===
// SSE would be ideal but the Fitbit companion sandbox may not support ReadableStream.
// Fall back to polling the chat list every 5 seconds.

let lastChatState = null;

async function pollForUpdates() {
  const response = await bridgeFetch("/chats");
  if (!response) return;

  try {
    const data = await response.json();
    const chats = data.chats || [];
    const currentState = JSON.stringify(chats.map(c => ({
      id: c.id,
      unread: c.unread_count,
      lastTs: c.last_message?.timestamp
    })));

    // Only push to watch if something changed
    if (currentState !== lastChatState) {
      lastChatState = currentState;
      sendToWatch({ type: "chat_list", chats });

      // Check for new unread messages and notify watch
      for (const chat of chats) {
        if (chat.unread_count > 0 && chat.last_message) {
          sendToWatch({
            type: "new_message",
            chatId: chat.id,
            chatName: chat.name,
            senderName: chat.last_message.sender_name,
            content: chat.last_message.content
          });
        }
      }
    }
  } catch (err) {
    console.error(`[companion] Poll parse failed: ${err.message}`);
  }
}

function startPolling() {
  if (pollTimer) return;
  console.log("[companion] Starting poll loop");
  pollTimer = setInterval(pollForUpdates, POLL_INTERVAL_MS);
  // Immediate first poll
  pollForUpdates();
}

function stopPolling() {
  if (pollTimer) {
    clearInterval(pollTimer);
    pollTimer = null;
    console.log("[companion] Stopped poll loop");
  }
}

// === Settings change listener (for pairing) ===

settingsStorage.addEventListener("change", (evt) => {
  if (evt.key === "bridge_token" && evt.newValue) {
    console.log("[companion] Auth token updated, starting poll");
    startPolling();
  }
});

// === Startup ===

// Try SSE first, fall back to polling
async function trySSE() {
  const token = getToken();
  if (!token) return false;

  try {
    // Attempt SSE connection via fetch with streaming
    const response = await fetch(`${BRIDGE_URL}/events`, {
      headers: { "Authorization": `Bearer ${token}` }
    });

    if (!response.ok || !response.body) {
      console.log("[companion] SSE not available, using polling");
      return false;
    }

    console.log("[companion] SSE connected");
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    // Read SSE stream
    while (true) {
      const { done, value } = await reader.read();
      if (done) {
        console.log("[companion] SSE stream ended, falling back to polling");
        return false;
      }

      buffer += decoder.decode(value, { stream: true });

      // Parse SSE events from buffer
      const lines = buffer.split("\n");
      buffer = lines.pop() || ""; // Keep incomplete line in buffer

      let eventType = null;
      for (const line of lines) {
        if (line.startsWith("event: ")) {
          eventType = line.substring(7).trim();
        } else if (line.startsWith("data: ") && eventType) {
          try {
            const eventData = JSON.parse(line.substring(6));
            handleSSEEvent(eventType, eventData);
          } catch (parseErr) {
            console.error(`[companion] SSE parse error: ${parseErr.message}`);
          }
          eventType = null;
        }
      }
    }
  } catch (err) {
    console.log(`[companion] SSE failed: ${err.message}, using polling`);
    return false;
  }
}

function handleSSEEvent(eventType, data) {
  switch (eventType) {
    case "new_message":
      sendToWatch({
        type: "new_message",
        chatId: data.chat_id,
        messageId: data.message_id,
        senderName: data.sender_name,
        content: data.content,
        timestamp: data.timestamp,
        isFromCurrentUser: data.is_from_current_user
      });
      break;
    case "chat_update":
      // Refresh the full chat list for simplicity
      fetchChats();
      break;
    default:
      console.log(`[companion] Unknown SSE event: ${eventType}`);
  }
}

// Initialize on companion startup
async function init() {
  console.log("[companion] OpenChat companion starting");

  if (!getToken()) {
    console.log("[companion] No auth token, waiting for pairing");
    return;
  }

  // Verify token is still valid
  const statusResp = await bridgeFetch("/auth/status");
  if (!statusResp) {
    console.log("[companion] Bridge not reachable, will retry via polling");
    startPolling();
    return;
  }

  // Try SSE, fall back to polling
  const sseWorked = await trySSE();
  if (!sseWorked) {
    startPolling();
  }
}

init();
