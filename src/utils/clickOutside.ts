// Robust "click backdrop to close" handler. Using a plain @click.self can still
// fire when the user drags from inside the panel to the backdrop (or vice
// versa), which feels like the dialog closes too easily. We only close when
// BOTH mousedown and mouseup land on the backdrop element itself.

export function useBackdropClose(onClose: () => void) {
  let mouseDownOnBackdrop = false

  function onMouseDown(e: Event) {
    const me = e as MouseEvent
    mouseDownOnBackdrop = me.target === me.currentTarget
  }
  function onMouseUp(e: Event) {
    const me = e as MouseEvent
    if (mouseDownOnBackdrop && me.target === me.currentTarget) {
      onClose()
    }
    mouseDownOnBackdrop = false
  }

  return { onMouseDown, onMouseUp }
}
