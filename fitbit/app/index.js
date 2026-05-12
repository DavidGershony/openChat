import * as messaging from "messaging";
import document from "document";
import { vibration } from "haptics";

// === State ===
let chats = [];
let currentChatId = null;
let currentChatName = null;
let messages = [];

const QUICK_REPLIES = [
  "OK",
  "On my way",
  "Busy",
  "Call me",
  "Yes",
  "No",
  "Thanks",
  "Love you",
  "\u{1F44D}",  // thumbs up
  "\u{2764}"    // heart
];

// === Screen Elements ===
const chatListScreen = document.getElementById("chat-list-screen");
const messageListScreen = document.getElementById("message-list-screen");
const replyScreen = document.getElementById("reply-screen");

const chatList = document.getElementById("chat-list");
const messageList = document.getElementById("message-list");
const replyList = document.getElementById("reply-list");

const loadingText = document.getElementById("loading-text");
const emptyState = document.getElementById("empty-state");
const msgChatName = document.getElementById("msg-chat-name");

const notificationToast = document.getElementById("notification-toast");
const toastSender = document.getElementById("toast-sender");
const toastContent = document.getElementById("toast-content");

// === Navigation ===

function showScreen(screen) {
  chatListScreen.style.display = screen === "chats" ? "inline" : "none";
  messageListScreen.style.display = screen === "messages" ? "inline" : "none";
  replyScreen.style.display = screen === "replies" ? "inline" : "none";
}

// Back buttons
document.getElementById("msg-back-btn").addEventListener("click", () => {
  currentChatId = null;
  showScreen("chats");
  sendToCompanion({ type: "get_chats" });
});

document.getElementById("reply-back-btn").addEventListener("click", () => {
  showScreen("messages");
});

// Reply button
document.getElementById("reply-btn").addEventListener("click", () => {
  showScreen("replies");
});

// === Chat List ===

function renderChatList() {
  loadingText.style.display = "none";

  if (chats.length === 0) {
    emptyState.style.display = "inline";
    return;
  }
  emptyState.style.display = "none";

  // Update total unread badge
  const totalUnread = chats.reduce((sum, c) => sum + (c.unread_count || 0), 0);
  const totalUnreadBg = document.getElementById("total-unread-bg");
  const totalUnreadText = document.getElementById("total-unread-text");
  if (totalUnread > 0) {
    totalUnreadBg.style.display = "inline";
    totalUnreadText.style.display = "inline";
    totalUnreadText.text = totalUnread > 99 ? "99+" : String(totalUnread);
  } else {
    totalUnreadBg.style.display = "none";
    totalUnreadText.style.display = "none";
  }

  chatList.delegate = {
    getTileInfo: (index) => ({
      type: "chat-item",
      value: chats[index],
      index: index
    }),
    configureTile: (tile, info) => {
      const chat = info.value;
      tile.getElementById("chat-name").text = truncate(chat.name || "Unknown", 18);

      const preview = chat.last_message
        ? `${chat.last_message.sender_name}: ${chat.last_message.content}`
        : "No messages";
      tile.getElementById("chat-preview").text = truncate(preview, 30);

      tile.getElementById("chat-time").text = formatTime(chat.last_message?.timestamp);

      // Unread badge
      const unreadBg = tile.getElementById("unread-bg");
      const unreadText = tile.getElementById("unread-text");
      if (chat.unread_count > 0) {
        unreadBg.style.display = "inline";
        unreadText.style.display = "inline";
        unreadText.text = chat.unread_count > 99 ? "99+" : String(chat.unread_count);
      } else {
        unreadBg.style.display = "none";
        unreadText.style.display = "none";
      }

      // Tap to open chat
      tile.addEventListener("click", () => {
        currentChatId = chat.id;
        currentChatName = chat.name;
        msgChatName.text = truncate(chat.name || "Chat", 16);
        showScreen("messages");
        sendToCompanion({ type: "get_messages", chatId: chat.id, limit: 20 });
        sendToCompanion({ type: "mark_read", chatId: chat.id });
      });
    }
  };
  chatList.length = chats.length;
}

// === Message List ===

function renderMessages() {
  if (messages.length === 0) return;

  messageList.delegate = {
    getTileInfo: (index) => ({
      type: "message-item",
      value: messages[index],
      index: index
    }),
    configureTile: (tile, info) => {
      const msg = info.value;

      tile.getElementById("msg-sender").text = truncate(msg.sender_name || "Unknown", 16);
      tile.getElementById("msg-content").text = truncate(msg.content || "", 55);
      tile.getElementById("msg-time").text = formatTime(msg.timestamp);

      // Highlight own messages
      const bg = tile.getElementById("message-bg") || tile.firstChild;
      if (bg && msg.is_from_current_user) {
        bg.class = "message-own-bg";
      }
    }
  };
  messageList.length = messages.length;
}

// === Quick Reply ===

function renderReplies() {
  replyList.delegate = {
    getTileInfo: (index) => ({
      type: "reply-item",
      value: QUICK_REPLIES[index],
      index: index
    }),
    configureTile: (tile, info) => {
      tile.getElementById("reply-text").text = info.value;

      tile.addEventListener("click", () => {
        if (!currentChatId) return;

        sendToCompanion({
          type: "send_message",
          chatId: currentChatId,
          content: info.value
        });

        // Brief vibration feedback
        vibration.start("confirmation");

        // Go back to messages
        showScreen("messages");

        // Optimistic: add the sent message to the local list
        messages.push({
          sender_name: "You",
          content: info.value,
          timestamp: new Date().toISOString(),
          is_from_current_user: true,
          type: "text"
        });
        renderMessages();
      });
    }
  };
  replyList.length = QUICK_REPLIES.length;
}

// === Notification Toast ===

let toastTimer = null;

function showNotification(senderName, content) {
  // Don't show if we're viewing that chat
  toastSender.text = truncate(senderName || "New message", 24);
  toastContent.text = truncate(content || "", 28);
  notificationToast.style.display = "inline";

  vibration.start("nudge");

  if (toastTimer) clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    notificationToast.style.display = "none";
    toastTimer = null;
  }, 4000);
}

// Tap toast to dismiss
notificationToast.addEventListener("click", () => {
  notificationToast.style.display = "none";
  if (toastTimer) {
    clearTimeout(toastTimer);
    toastTimer = null;
  }
});

// === Companion Messaging ===

function sendToCompanion(data) {
  if (messaging.peerSocket.readyState === messaging.peerSocket.OPEN) {
    messaging.peerSocket.send(JSON.stringify(data));
  }
}

messaging.peerSocket.addEventListener("message", (evt) => {
  let msg;
  try {
    msg = typeof evt.data === "string" ? JSON.parse(evt.data) : evt.data;
  } catch (err) {
    console.error(`[watch] Invalid message: ${err.message}`);
    return;
  }

  switch (msg.type) {
    case "chat_list":
      chats = msg.chats || [];
      renderChatList();
      break;

    case "messages":
      if (msg.chatId === currentChatId) {
        messages = msg.messages || [];
        renderMessages();
      }
      break;

    case "new_message":
      // Show notification if not viewing that chat
      if (msg.chatId !== currentChatId && !msg.isFromCurrentUser) {
        showNotification(msg.senderName, msg.content);
      }
      // If viewing this chat, refresh messages
      if (msg.chatId === currentChatId) {
        sendToCompanion({ type: "get_messages", chatId: currentChatId, limit: 20 });
      }
      break;

    case "send_result":
      if (!msg.success) {
        vibration.start("alert");
      }
      break;

    case "auth_error":
      loadingText.text = "Not paired. Open Fitbit settings.";
      loadingText.style.display = "inline";
      break;

    default:
      console.log(`[watch] Unknown message type: ${msg.type}`);
  }
});

messaging.peerSocket.addEventListener("open", () => {
  console.log("[watch] Companion connected");
  sendToCompanion({ type: "get_chats" });
});

messaging.peerSocket.addEventListener("close", () => {
  console.log("[watch] Companion disconnected");
});

// === Helpers ===

function truncate(str, maxLen) {
  if (!str) return "";
  return str.length > maxLen ? str.substring(0, maxLen - 1) + "\u2026" : str;
}

function formatTime(isoString) {
  if (!isoString) return "";
  try {
    const date = new Date(isoString);
    const now = new Date();
    const hours = date.getHours().toString().padStart(2, "0");
    const minutes = date.getMinutes().toString().padStart(2, "0");

    // Same day: show time. Different day: show date.
    if (date.toDateString() === now.toDateString()) {
      return `${hours}:${minutes}`;
    }
    const month = (date.getMonth() + 1).toString();
    const day = date.getDate().toString();
    return `${month}/${day}`;
  } catch {
    return "";
  }
}

// === Init ===

showScreen("chats");
renderReplies(); // Static list, render once
console.log("[watch] OpenChat watch app started");
