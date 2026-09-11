ALTER TABLE hm_messages ADD messagekeywords nvarchar(500) NOT NULL DEFAULT ''

update hm_dbversion set value = 6036
