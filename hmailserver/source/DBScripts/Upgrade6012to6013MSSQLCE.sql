ALTER TABLE hm_messages ADD messagesavedate datetime not null DEFAULT '2001-01-01'

update hm_messages set messagesavedate = messagecreatetime

update hm_dbversion set value = 6013
