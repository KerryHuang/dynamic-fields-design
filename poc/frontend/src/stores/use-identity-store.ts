import { defineStore } from 'pinia';
import { ref } from 'vue';
import type { User } from 'src/domain/dynamic/types';

const STORAGE_KEY = 'dynamic.userId';

function load(): string {
  try {
    return localStorage.getItem(STORAGE_KEY) || 'admin';
  } catch {
    return 'admin';
  }
}

export const useIdentityStore = defineStore('identity', () => {
  const userId = ref(load());
  /** Users from GET /users, filled by syncUsers. */
  const users = ref<User[]>([]);

  function setUser(id: string) {
    userId.value = id;
    try {
      localStorage.setItem(STORAGE_KEY, id);
    } catch {
      /* storage unavailable: keep in-memory only */
    }
  }

  /** The user list comes from the API; a stored id that is not in it falls back to the first user. Returns whether userId changed. */
  function syncUsers(list: User[]): boolean {
    users.value = list;
    if (!list.length || list.some((u) => u.userId === userId.value)) return false;
    setUser(list[0]!.userId);
    return true;
  }

  return { userId, users, setUser, syncUsers };
});
