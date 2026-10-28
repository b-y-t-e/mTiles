// Where a browser keeps what makes it the same machine tomorrow.
//
// The identity key is generated once and kept, so a reload is a reconnection
// rather than a new machine asking to be paired again. IndexedDB rather than
// localStorage because it holds bytes without a round trip through base64,
// and because it survives what localStorage does.

const DB_NAME = "tailcat-link";
const STORE = "links";

function openDb() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, 1);
    request.onupgradeneeded = () => request.result.createObjectStore(STORE);
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function withStore(mode, work) {
  const db = await openDb();
  try {
    return await new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, mode);
      const result = work(tx.objectStore(STORE));
      tx.oncomplete = () => resolve(result.result ?? null);
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
  } finally {
    db.close();
  }
}

export const IndexedDbStore = {
  load: (appName) => withStore("readonly", (store) => store.get(appName)),
  save: (appName, state) => withStore("readwrite", (store) => store.put(state, appName)),
  remove: (appName) => withStore("readwrite", (store) => store.delete(appName)),
};

/// For a page that would rather not persist anything, and for tests.
export function memoryStore() {
  const held = new Map();
  return {
    load: async (appName) => held.get(appName) ?? null,
    save: async (appName, state) => void held.set(appName, state),
    remove: async (appName) => void held.delete(appName),
  };
}
