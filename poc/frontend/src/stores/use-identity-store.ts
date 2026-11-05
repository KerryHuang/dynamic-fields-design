import { defineStore } from 'pinia';
import { ref } from 'vue';
import type { User } from 'src/domain/dynamic/types';

/** Session identity. The cookie is the real session; this only mirrors who is signed in ('' = signed out). */
export const useIdentityStore = defineStore('identity', () => {
  const userId = ref('');
  const mustChangePassword = ref(false);
  /** Users from GET /users, filled by MainLayout. */
  const users = ref<User[]>([]);

  function setSession(id: string, mustChange = false) {
    userId.value = id;
    mustChangePassword.value = mustChange;
  }

  function setUsers(list: User[]) {
    users.value = list;
  }

  function clear() {
    userId.value = '';
    mustChangePassword.value = false;
    users.value = [];
  }

  return { userId, mustChangePassword, users, setSession, setUsers, clear };
});
