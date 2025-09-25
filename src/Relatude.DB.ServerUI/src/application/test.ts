import { get } from 'mobx'
import { create } from 'zustand'

type Store = {
  count: number,
  count2: number,
  inc: () => void
  inc2: () => void
  sum: ()=> number
  //sum2: number,
}

export const useStore = create<Store>()((set,get) => ({
  count: 1,
  count2: 2,
  sum: () => get().count + get().count2,
  inc: () => set((state) => ({ count: state.count + 1 })),
  inc2: () => set((state) => ({ count2: state.count2 + 1 })),
}))

