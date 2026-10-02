local uv = vim.uv or vim.loop
local run_dir = vim.env.NVIM_BENCH_RUN_DIR
local expected_lines = tonumber(vim.env.NVIM_BENCH_LINES or "")
local profile = vim.env.NVIM_BENCH_PROFILE or "plain"
local launch_open_ns = tonumber(_G.nvim_scroll_bench_open_ns)
local initial_ready_ns

local function json(value)
  return vim.fn.json_encode(value)
end

local function write_atomic(path, contents)
  local temp = path .. ".tmp"
  local file, err = io.open(temp, "w")
  if not file then error(err) end
  file:write(contents)
  file:close()
  local ok, rename_err = os.rename(temp, path)
  if not ok then error(rename_err) end
end

local function emit(record)
  local file, err = io.open(run_dir .. "/events.jsonl", "a")
  if not file then error(err) end
  file:write(json(record), "\n")
  file:close()
end

local function geometry()
  return vim.o.columns, vim.o.lines
end

local function ready()
  local cols, rows = geometry()
  local payload = { pid = uv.os_getpid(), cols = cols, rows = rows, line_count = vim.api.nvim_buf_line_count(0),
    initial_ready_ns = initial_ready_ns }
  if launch_open_ns and initial_ready_ns then
    payload.file_open_to_ready_ns = initial_ready_ns - launch_open_ns
  end
  write_atomic(run_dir .. "/ready.json", json(payload))
end

local function fail(message)
  pcall(emit, { type = "error", monotonic_ns = uv.hrtime(), message = tostring(message) })
  vim.schedule(function() vim.cmd("cquit 1") end)
end

local ok, err = xpcall(function()
  assert(run_dir and run_dir ~= "", "NVIM_BENCH_RUN_DIR is required")
  assert(expected_lines and expected_lines > 0, "NVIM_BENCH_LINES must be a positive integer")
  assert(expected_lines % 1 == 0, "NVIM_BENCH_LINES must be an integer")
  assert(profile == "plain" or profile == "syntax", "NVIM_BENCH_PROFILE must be plain or syntax")
  assert(vim.fn.filereadable(run_dir .. "/go") == 0, "go gate must not exist before workload starts")
  assert(type(vim.api.nvim__redraw) == "function", "nvim__redraw requires Neovim 0.10 or newer")
  local buf = vim.api.nvim_get_current_buf()
  assert(vim.bo[buf].buftype == "", "fixture must be a normal file buffer")
  local line_count = vim.api.nvim_buf_line_count(buf)
  assert(line_count == expected_lines, ("fixture line count mismatch: expected %d, found %d"):format(expected_lines, line_count))
  vim.api.nvim_win_set_cursor(0, { 1, 0 })
  vim.o.laststatus = 2
  vim.o.wrap = false
  vim.o.scrolloff = 0
  vim.o.termguicolors = true
  vim.o.smoothscroll = false
  vim.o.lazyredraw = false
  vim.wo.number = false
  vim.wo.relativenumber = false
  if profile == "syntax" then
    vim.cmd("setfiletype c")
    vim.cmd("syntax enable")
  else
    vim.cmd("syntax off")
    vim.bo[buf].filetype = ""
  end

  local function hl(name, color)
    vim.api.nvim_set_hl(0, name, { fg = color, bg = color })
  end
  hl("NvimScrollBenchSyncBlue", "#0000ff")
  hl("NvimScrollBenchSyncWhite", "#ffffff")
  hl("NvimScrollBenchZero", "#00ffff")
  hl("NvimScrollBenchOne", "#ff00ff")
  vim.api.nvim_set_hl(0, "NvimScrollBenchEof", { fg = "#000000", bg = "#ff8800" })

  local function cell(group)
    return "%#" .. group .. "# %*"
  end
  local function bits(value, count)
    local out = ""
    for bit = count - 1, 0, -1 do
      local divisor = 2 ^ bit
      local one = math.floor(value / divisor) % 2
      out = out .. cell(one == 1 and "NvimScrollBenchOne" or "NvimScrollBenchZero")
    end
    return out
  end
  local function statusline(line, phase)
    local parity = 0
    for bit = 31, 0, -1 do parity = (parity + math.floor(line / (2 ^ bit)) % 2) % 2 end
    for bit = 1, 0, -1 do parity = (parity + math.floor(phase / (2 ^ bit)) % 2) % 2 end
    return cell("NvimScrollBenchSyncBlue") .. cell("NvimScrollBenchSyncWhite") ..
      cell("NvimScrollBenchSyncBlue") .. cell("NvimScrollBenchSyncWhite") ..
      bits(line, 32) .. bits(phase, 2) .. bits(parity, 1) ..
      cell("NvimScrollBenchSyncBlue") .. cell("NvimScrollBenchSyncWhite") ..
      cell("NvimScrollBenchSyncBlue") .. cell("NvimScrollBenchSyncWhite") .. " %l/%L"
  end
  vim.wo.statusline = statusline(1, 0)

  local eof_ns = vim.api.nvim_create_namespace("nvim_scroll_bench_eof")
  local last_line = vim.api.nvim_buf_get_lines(buf, line_count - 1, line_count, false)[1]
  vim.api.nvim_buf_set_extmark(buf, eof_ns, line_count - 1, 0, {
    end_col = #last_line, hl_group = "NvimScrollBenchEof", hl_eol = true, priority = 200,
  })

  vim.api.nvim__redraw({ statusline = true, flush = true, valid = true })
  initial_ready_ns = uv.hrtime()
  ready()
  local ready_timer = uv.new_timer()
  ready_timer:start(100, 100, vim.schedule_wrap(function()
    if vim.fn.filereadable(run_dir .. "/go") == 0 then ready() end
  end))

  local function wait_for_go()
    if vim.fn.filereadable(run_dir .. "/go") == 0 then
      vim.defer_fn(wait_for_go, 10)
      return
    end
    ready_timer:stop()
    ready_timer:close()
    vim.api.nvim_win_set_cursor(0, { 1, 0 })
    local start_ns = uv.hrtime()
    local steps = 0
    vim.wo.statusline = statusline(1, 1)
    emit({ type = "start", monotonic_ns = start_ns, line = 1, steps = 0 })
    vim.api.nvim__redraw({ statusline = true, flush = true, valid = true })

    local function step()
      if steps < expected_lines - 1 then
        local before = vim.api.nvim_win_get_cursor(0)[1]
        if before ~= steps + 1 then
          fail(("unexpected cursor before step %d: expected line %d, found %d"):format(steps, steps + 1, before))
          return
        end
        vim.cmd("normal! j")
        local after = vim.api.nvim_win_get_cursor(0)[1]
        if after ~= before + 1 then
          fail(("movement invariant failed at step %d/%d: line %d moved to %d"):format(steps, expected_lines - 1, before, after))
          return
        end
        vim.wo.statusline = statusline(after, 1)
        vim.api.nvim__redraw({ statusline = true, flush = true, valid = true })
        steps = steps + 1
        if steps % 256 == 0 then
          emit({ type = "progress", monotonic_ns = uv.hrtime(), line = after, steps = steps })
        end
        vim.defer_fn(step, 0)
      else
        vim.wo.statusline = statusline(vim.api.nvim_win_get_cursor(0)[1], 2)
        vim.api.nvim__redraw({ statusline = true, flush = true, valid = true })
        local end_ns = uv.hrtime()
        local cols, rows = geometry()
        emit({ type = "end", monotonic_ns = end_ns, duration_ns = end_ns - start_ns,
          final_line = vim.api.nvim_win_get_cursor(0)[1], steps = steps, rows = rows, cols = cols })
        local function wait_release()
          if vim.fn.filereadable(run_dir .. "/release") == 0 then
            vim.defer_fn(wait_release, 10)
            return
          end
          vim.cmd("qa!")
        end
        wait_release()
      end
    end
    vim.defer_fn(step, 0)
  end
  vim.defer_fn(wait_for_go, 10)
end, debug.traceback)
if not ok then fail(err) end
