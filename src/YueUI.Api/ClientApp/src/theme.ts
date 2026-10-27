import base from '@primeuix/themes/aura/base'
import badge from '@primeuix/themes/aura/badge'
import button from '@primeuix/themes/aura/button'
import card from '@primeuix/themes/aura/card'
import checkbox from '@primeuix/themes/aura/checkbox'
import confirmdialog from '@primeuix/themes/aura/confirmdialog'
import contextmenu from '@primeuix/themes/aura/contextmenu'
import dataview from '@primeuix/themes/aura/dataview'
import dialog from '@primeuix/themes/aura/dialog'
import divider from '@primeuix/themes/aura/divider'
import fieldset from '@primeuix/themes/aura/fieldset'
import floatlabel from '@primeuix/themes/aura/floatlabel'
import iconfield from '@primeuix/themes/aura/iconfield'
import inputgroup from '@primeuix/themes/aura/inputgroup'
import inputnumber from '@primeuix/themes/aura/inputnumber'
import inputtext from '@primeuix/themes/aura/inputtext'
import menu from '@primeuix/themes/aura/menu'
import menubar from '@primeuix/themes/aura/menubar'
import message from '@primeuix/themes/aura/message'
import paginator from '@primeuix/themes/aura/paginator'
import progressbar from '@primeuix/themes/aura/progressbar'
import radiobutton from '@primeuix/themes/aura/radiobutton'
import panel from '@primeuix/themes/aura/panel'
import rating from '@primeuix/themes/aura/rating'
import ripple from '@primeuix/themes/aura/ripple'
import select from '@primeuix/themes/aura/select'
import selectbutton from '@primeuix/themes/aura/selectbutton'
import slider from '@primeuix/themes/aura/slider'
import tabs from '@primeuix/themes/aura/tabs'
import tag from '@primeuix/themes/aura/tag'
import textarea from '@primeuix/themes/aura/textarea'
import timeline from '@primeuix/themes/aura/timeline'
import togglebutton from '@primeuix/themes/aura/togglebutton'
import tooltip from '@primeuix/themes/aura/tooltip'
import virtualscroller from '@primeuix/themes/aura/virtualscroller'

/**
 * The Aura preset with only the design tokens of the components this app uses. `@primeuix/themes/aura` carries
 * those of all ~100 PrimeVue components, used or not, and cannot be tree-shaken, since they are values of one
 * object. The list also names what the registered components pull in themselves (Select brings VirtualScroller,
 * DataView the Paginator with InputNumber, SelectButton the ToggleButton, …). A missing entry leaves that component
 * unstyled without any error, so `vite build` checks that every component style in the bundle has its tokens here
 * (see `presetCoversStyles` in vite.config.ts).
 */
export default {
  ...base,
  components: {
    badge,
    button,
    card,
    checkbox,
    confirmdialog,
    contextmenu,
    dataview,
    dialog,
    divider,
    fieldset,
    floatlabel,
    iconfield,
    inputgroup,
    inputnumber,
    inputtext,
    menu,
    menubar,
    message,
    paginator,
    progressbar,
    radiobutton,
    panel,
    rating,
    ripple,
    select,
    selectbutton,
    slider,
    tabs,
    tag,
    textarea,
    timeline,
    togglebutton,
    tooltip,
    virtualscroller,
  },
}
