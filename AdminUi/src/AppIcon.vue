<script setup>
defineProps({ name: { type: String, required: true } });
</script>

<template>
  <svg class="ui-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
    <template v-if="name === 'store'">
      <path d="M4 9.2 5.7 4h12.6L20 9.2" />
      <path d="M3.4 9.2h17.2v2.1a2.3 2.3 0 0 1-4.6 0 2.3 2.3 0 0 1-4.6 0 2.3 2.3 0 0 1-4.6 0 2.3 2.3 0 0 1-3.4 2" />
      <path d="M5.4 14.2V20h13.2v-5.8M8.3 20v-5h7.4v5" />
      <path d="M6.2 6.7h11.6" opacity=".45" />
      <rect x="9.4" y="15.9" width="5.2" height="3.1" rx=".5" fill="currentColor" stroke="none" opacity=".2" />
    </template>
    <template v-else-if="name === 'redeem'">
      <path d="M3 10.2V5.8A2.8 2.8 0 0 1 5.8 3h12.4A2.8 2.8 0 0 1 21 5.8v4.4a2.1 2.1 0 0 0 0 4.2v3.8a2.8 2.8 0 0 1-2.8 2.8H5.8A2.8 2.8 0 0 1 3 18.2v-3.8a2.1 2.1 0 0 0 0-4.2Z" fill="currentColor" opacity=".13" />
      <path d="M3 10.2V5.8A2.8 2.8 0 0 1 5.8 3h12.4A2.8 2.8 0 0 1 21 5.8v4.4a2.1 2.1 0 0 0 0 4.2v3.8a2.8 2.8 0 0 1-2.8 2.8H5.8A2.8 2.8 0 0 1 3 18.2v-3.8a2.1 2.1 0 0 0 0-4.2Z" />
      <path d="M9 3v3m0 3v2m0 3v2m0 3v2" stroke-dasharray="1 2" />
      <path d="m15.3 7.7.9 1.9 2.1.3-1.5 1.5.4 2.1-1.9-1-1.9 1 .4-2.1-1.5-1.5 2.1-.3Z" fill="currentColor" opacity=".7" />
    </template>
    <template v-else-if="name === 'home'"><rect x="3" y="3" width="8" height="8" rx="2" fill="currentColor" stroke="none" /><rect x="14" y="3" width="7" height="12" rx="2" fill="currentColor" opacity=".25" /><rect x="3" y="14" width="8" height="7" rx="2" fill="currentColor" opacity=".25" /><rect x="14" y="18" width="7" height="3" rx="1.5" fill="currentColor" stroke="none" /></template>
    <template v-else-if="name === 'users'"><circle cx="9" cy="8" r="4" fill="currentColor" stroke="none" /><path d="M2 21v-3a7 7 0 0 1 14 0v3Z" fill="currentColor" opacity=".25" /><path d="M16 4a4 4 0 0 1 0 8m2 3a5 5 0 0 1 4 5" /></template>
    <template v-else-if="name === 'matches'"><path d="M7 6h10c3 0 4 3 5 10 .5 4-3 5-5 2l-2-2H9l-2 2c-2 3-5 2-5-2C3 9 4 6 7 6Z" fill="currentColor" opacity=".2" /><path d="M7 9v6m-3-3h6" /><circle cx="16" cy="10" r="1.5" fill="currentColor" stroke="none" /><circle cx="19" cy="13" r="1.5" fill="currentColor" stroke="none" /></template>
    <template v-else-if="name === 'content'"><rect x="3" y="3" width="18" height="18" rx="3" fill="currentColor" opacity=".18" /><rect x="6" y="6" width="5" height="5" rx="1" fill="currentColor" stroke="none" /><path d="M14 7h4m-4 4h4M6 15h12M6 18h8" /></template>
    <template v-else-if="name === 'settings'"><rect x="3" y="3" width="18" height="18" rx="3" fill="currentColor" opacity=".18" /><path d="M6 8h12M6 16h12" /><rect x="8" y="5" width="3" height="6" rx="1" fill="currentColor" stroke="none" /><rect x="14" y="13" width="3" height="6" rx="1" fill="currentColor" stroke="none" /></template>
    <template v-else-if="name === 'cpu'"><rect x="5" y="5" width="14" height="14" rx="3" fill="currentColor" opacity=".2" /><rect x="9" y="9" width="6" height="6" rx="1" fill="currentColor" stroke="none" /><path d="M9 2v3m6-3v3M9 19v3m6-3v3M2 9h3m-3 6h3m14-6h3m-3 6h3" /></template>
    <template v-else-if="name === 'sun'"><circle cx="12" cy="12" r="4" /><path d="M12 2v2M12 20v2M2 12h2M20 12h2M5 5l1 1M18 18l1 1M5 19l1-1M18 6l1-1" /></template>
    <template v-else-if="name === 'logout'"><path d="M10 4H4v16h6M8 12h13m-4-4 4 4-4 4" /></template>
    <template v-else-if="name === 'menu'"><path d="M4 6h16M4 12h16M4 18h16" /></template>
    <template v-else><path d="M4 4h16v16H4z" /></template>
  </svg>
</template>
